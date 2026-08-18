using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Web;

/// <summary>
/// Gemini API 의 Google Search grounding 경유 웹검색(POST /v1beta/interactions, tools=[google_search]).
/// 키는 GEMINI_API_KEY, 모델은 GEMINI_SEARCH_MODEL(기본 gemini-3.7-flash).
///
/// grounding 은 랭킹된 결과 목록을 주지 않는다 — 모델이 검색해서 만든 답변과 그 답변에 달린
/// 출처(annotations 의 url_citation)만 돌아온다. 그래서 이 툴의 출력은 '요약 답변 + 출처 URL' 이고,
/// 원문이 필요하면 호출자가 WebFetch 로 이어서 읽는다.
/// </summary>
public sealed class WebSearchTool : ITool
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";
    // 최신(3.7)은 수요가 몰려 500 "high demand" 가 잦다. 무료 할당량은 3.x 전체가 공유하므로
    // 한 단계 아래를 기본으로 둔다. 바꾸려면 GEMINI_SEARCH_MODEL.
    private const string DefaultModel = "gemini-3.6-flash";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public string Name => "WebSearch";

    public string Description => """
        Searches the web via Google and returns a grounded summary with its source URLs.

        Usage:
        - Provide a natural-language query. The search is run by Google; the answer cites the pages it used.
        - This returns a summary plus sources, NOT a ranked result list. To read a source in full, follow up with WebFetch.
        - Use it for facts that may have changed since training, or to find the page you then need to read.
        """;

    public bool IsReadOnly => true;

    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query" }
          },
          "required": ["query"]
        }
        """);

    private sealed record Input([property: JsonPropertyName("query")] string? Query);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Query))
        {
            yield return new ToolOutput("WebSearch: 'query' is required", IsError: true);
            yield break;
        }

        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(L10n.Get("tools.webSearch.noKey"), IsError: true);
            yield break;
        }

        var model = Environment.GetEnvironmentVariable("GEMINI_SEARCH_MODEL");
        if (string.IsNullOrWhiteSpace(model))
        {
            model = DefaultModel;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        string? body = null;
        try
        {
            body = await CallAsync(key!, model!, inp.Query!, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.webSearch.timeout", Timeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.webSearch.requestFailed", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var rendered = Render(body!);
        // 검색 결과(외부 콘텐츠)는 신뢰불가 — 인젝션 경계를 앞에 붙인다.
        yield return rendered.Length == 0
            ? new ToolOutput("(no results)")
            : new ToolOutput(Reminders.UntrustedToolOutput + rendered);
    }

    private static async Task<string> CallAsync(string key, string model, string query, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("x-goog-api-key", key);

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["input"] = query,
            ["tools"] = new object[] { new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "google_search" } },
        };

        using var resp = await client.PostAsJsonAsync(Endpoint, payload, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 본문에 사유(잘못된 키·쿼터 초과·모델명 오류)가 담겨 오므로 잘라서 그대로 노출한다.
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        }

        return text;
    }

    /// <summary>
    /// 응답에서 답변 텍스트와 출처(url_citation)를 뽑아 사람이 읽을 형태로 만든다.
    /// 응답 스키마가 바뀌어도 죽지 않도록 이름으로 훑는 방어적 파싱을 쓴다.
    /// </summary>
    private static string Render(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (doc)
        {
            var answer = new StringBuilder();
            var sources = new List<(string Url, string Title)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Walk(doc.RootElement, answer, sources, seen);

            var sb = new StringBuilder();
            if (answer.Length > 0)
            {
                sb.AppendLine(answer.ToString().Trim());
            }

            if (sources.Count > 0)
            {
                sb.AppendLine().AppendLine("Sources:");
                var i = 1;
                foreach (var (url, title) in sources)
                {
                    sb.Append(i++).Append(". ").AppendLine(string.IsNullOrWhiteSpace(title) ? url : title);
                    sb.Append("   ").AppendLine(url);
                }
            }

            return sb.ToString().TrimEnd();
        }
    }

    // model_output 의 text 블록과 url_citation 주석을 재귀로 모은다.
    private static void Walk(
        JsonElement el, StringBuilder answer, List<(string, string)> sources, HashSet<string> seen)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                if (el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String
                    && el.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                    && ty.GetString() is "url_citation")
                {
                    var url = u.GetString()!;
                    if (seen.Add(url))
                    {
                        sources.Add((url,
                            el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                                ? t.GetString() ?? "" : ""));
                    }
                }
                else if (el.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                         && bt.GetString() is "text"
                         && el.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                {
                    answer.AppendLine(tx.GetString());
                }

                foreach (var p in el.EnumerateObject())
                {
                    Walk(p.Value, answer, sources, seen);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    Walk(item, answer, sources, seen);
                }

                break;
        }
    }
}
