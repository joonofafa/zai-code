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
/// z.ai 내장 web_search 툴 경유 웹검색. 대화 모델과 같은 엔드포인트(chat/completions)에
/// tools=[{type:web_search}] 를 실어 보내고, 응답 최상위 `web_search` 배열(제목·URL·본문·발행일)을
/// 결과 목록으로 돌려준다 — 별도 검색 키가 필요 없다(OPENAI_API_KEY/OPENAI_BASE_URL 재사용).
///
/// 예전엔 Gemini 의 Google Search grounding 을 썼는데, 그쪽은 (a) 결과 목록이 아니라 모델 답변의
/// 근거 표시라 제목 없이 리다이렉트 URL 만 오고, (b) 검색 여부를 모델이 재량으로 정해 "검색해줘"
/// 라고 해도 0건이 나오는 경우가 있었다. 검색 도구로는 z.ai 쪽이 안정적이라 교체했다.
/// </summary>
public sealed class WebSearchTool : ITool
{
    private const string DefaultSearchEngine = "search-prime";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public string Name => "WebSearch";

    public string Description => """
        Searches the web and returns ranked results (title, url, snippet, publish date).

        Usage:
        - Provide a natural-language query. Optional: limit (1-50, default 5).
        - Use it for facts that may have changed since training, or to find a page to read.
        - To read a result in full, follow up with WebFetch on its url.
        """;

    public bool IsReadOnly => true;

    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query" },
            "limit": { "type": "integer", "description": "Max results (1-50, default 5)" }
          },
          "required": ["query"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("limit")] int? Limit);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Query))
        {
            yield return new ToolOutput("WebSearch: 'query' is required", IsError: true);
            yield break;
        }

        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(baseUrl))
        {
            yield return new ToolOutput(L10n.Get("tools.webSearch.noKey"), IsError: true);
            yield break;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        string? body = null;
        try
        {
            body = await CallAsync(baseUrl!, key!, inp, timeoutCts.Token).ConfigureAwait(false);
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

    private static async Task<string> CallAsync(string baseUrl, string key, Input inp, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var count = Math.Clamp(inp.Limit ?? 5, 1, 50);
        var model = Environment.GetEnvironmentVariable("MOAI_MODEL")
                    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                    ?? "glm-5.3";

        // 검색 결과만 필요하므로 모델 답변은 최소로 자른다(max_tokens=1). 결과는 답변이 아니라
        // 최상위 web_search 배열로 오기 때문에, 토큰을 더 써도 얻는 게 없다.
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["max_tokens"] = 1,
            ["messages"] = new object[]
            {
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["content"] = inp.Query!,
                },
            },
            ["tools"] = new object[]
            {
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "web_search",
                    ["web_search"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["enable"] = "True",
                        ["search_engine"] = Environment.GetEnvironmentVariable("MOAI_SEARCH_ENGINE")
                                            ?? DefaultSearchEngine,
                        ["search_result"] = "True",
                        ["count"] = count.ToString(),
                    },
                },
            },
        };

        using var resp = await client
            .PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/chat/completions", payload, ct)
            .ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            // 본문에 사유(키/쿼터/모델명)가 담겨 오므로 잘라서 그대로 노출한다.
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        }

        return text;
    }

    /// <summary>응답 최상위 web_search 배열을 사람이 읽을 목록으로. 스키마가 바뀌어도 죽지 않게 방어적으로 읽는다.</summary>
    public static string Render(string body)
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
            if (!doc.RootElement.TryGetProperty("web_search", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var i = 1;
            foreach (var r in arr.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = Str(r, "title");
                var link = Str(r, "link");
                if (title.Length == 0 && link.Length == 0)
                {
                    continue;
                }

                sb.Append(i++).Append(". ").AppendLine(title.Length > 0 ? title : link);
                if (link.Length > 0)
                {
                    sb.Append("   ").AppendLine(link);
                }

                var date = Str(r, "publish_date");
                var content = Str(r, "content");
                if (content.Length > 0)
                {
                    sb.Append("   ").AppendLine(date.Length > 0 ? $"[{date}] {content}" : content);
                }
                else if (date.Length > 0)
                {
                    sb.Append("   ").AppendLine($"[{date}]");
                }

                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? string.Empty).Trim()
                : string.Empty;
    }
}
