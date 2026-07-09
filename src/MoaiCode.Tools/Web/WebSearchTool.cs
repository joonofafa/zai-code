using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Web;

/// <summary>
/// open-moai 경유 웹검색. 로그인 시 저장된 baseUrl(OPENAI_BASE_URL) + API 키(OPENAI_API_KEY)로
/// POST {baseUrl}/search 를 호출한다(별도 검색 키 불필요 — B2B). 서버가 SearXNG 등 멀티엔진으로 검색.
/// 안전장치: 30초 타임아웃, ct 준수, 프록시 자동 적용. 엔드포인트 미배포(404) 시 친절한 안내.
/// </summary>
public sealed class WebSearchTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "WebSearch";

    public string Description => """
        Searches the web via the connected open-moai server and returns ranked results (title, url, snippet).

        Usage:
        - Provide a natural-language query. Optional: limit, language (e.g. "ko"), country (e.g. "KR").
        - Requires being logged in to open-moai (baseUrl + API key). No separate search API key needed.
        - Use WebFetch to read the full content of a result URL.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query" },
            "limit": { "type": "integer", "description": "Max results (default 10)" },
            "language": { "type": "string", "description": "Language code, e.g. ko, en" },
            "country": { "type": "string", "description": "Country code, e.g. KR, US" }
          },
          "required": ["query"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("limit")] int? Limit,
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("country")] string? Country);

    private sealed record SearchResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("snippet")] string? Snippet,
        [property: JsonPropertyName("engine")] string? Engine);

    private sealed record SearchResponse(
        [property: JsonPropertyName("results")] List<SearchResult>? Results,
        [property: JsonPropertyName("count")] int? Count);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Query))
        {
            yield return new ToolOutput("WebSearch: 'query' is required", IsError: true);
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                "WebSearch: open-moai 연결 정보가 없습니다. `moai login` 으로 로그인하세요 (baseUrl/API 키 필요).",
                IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/search";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        var tok = timeoutCts.Token;

        string? error = null;
        SearchResponse? data = null;
        try
        {
            data = await CallAsync(url, key, inp, tok).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = "WebSearch: 이 서버에 검색 엔드포인트(/api/v1/search)가 없습니다. " +
                    "open-moai 측에 웹검색 API(POST /api/v1/search) 배포가 필요합니다.";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = $"WebSearch: {Timeout.TotalSeconds:0}초 타임아웃";
        }
        catch (HttpRequestException ex)
        {
            error = $"WebSearch: 요청 실패 — {ex.Message}";
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var results = data?.Results ?? new List<SearchResult>();
        if (results.Count == 0)
        {
            yield return new ToolOutput("(no results)");
            yield break;
        }

        var sb = new StringBuilder();
        var i = 1;
        foreach (var r in results)
        {
            // engine 을 함께 표기한다 — 어떤 검색 엔진이 준 결과인지 알아야 품질 문제를 진단/판별할 수 있다.
            var engine = string.IsNullOrWhiteSpace(r.Engine) ? "" : $" [{r.Engine}]";
            sb.Append(i++).Append(". ").Append(r.Title ?? r.Url ?? "(untitled)").AppendLine(engine);
            if (!string.IsNullOrWhiteSpace(r.Url))
            {
                sb.Append("   ").AppendLine(r.Url);
            }

            if (!string.IsNullOrWhiteSpace(r.Snippet))
            {
                sb.Append("   ").AppendLine(r.Snippet!.Trim());
            }

            sb.AppendLine();
        }

        // 검색 결과(외부 콘텐츠)는 신뢰불가 — 인젝션 경계를 앞에 붙인다.
        yield return new ToolOutput(Reminders.UntrustedToolOutput + sb.ToString().TrimEnd());
    }

    private static async Task<SearchResponse?> CallAsync(string url, string key, Input inp, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var payload = new
        {
            query = inp.Query,
            limit = inp.Limit ?? 10,
            language = string.IsNullOrWhiteSpace(inp.Language) ? "ko" : inp.Language,
            country = string.IsNullOrWhiteSpace(inp.Country) ? "KR" : inp.Country,
        };

        using var resp = await client.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointMissingException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<SearchResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class EndpointMissingException : Exception
    {
    }
}
