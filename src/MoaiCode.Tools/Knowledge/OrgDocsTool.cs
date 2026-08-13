using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// open-moai 조직 문서함(개인 private + 소속 조직 문서) 하이브리드 RAG 검색.
/// 로그인 시 저장된 baseUrl(OPENAI_BASE_URL) + API 키(OPENAI_API_KEY)로
/// POST {baseUrl}/knowledge/search 를 호출한다(별도 키 불필요 — 채팅과 동일 키).
/// 계약: docs/SERVER_TASK_KNOWLEDGE_SEARCH.md. 서버 엔드포인트가 없을 때(404) 친절히 안내.
/// 결과는 외부 데이터라 UntrustedToolOutput 경계로 감싸 주입(프롬프트 인젝션 방어).
/// </summary>
public sealed class OrgDocsTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "OrgDocs";

    public string Description => """
        Searches the user's personal and organization knowledge base on the connected
        open-moai server and returns relevant document snippets (title, snippet, scope, score).

        Use this when the task references INTERNAL/ORGANIZATIONAL knowledge that is NOT in the codebase:
        company guidelines, API/integration specs, internal policies, design docs, prior decisions, etc.
        Prefer this over guessing or WebSearch when the answer likely lives in the org's documents.

        Input:
        - query (required): natural-language query.
        - scope (optional): "personal" (your private docs) | "organization" (your orgs' shared docs) |
          "all" (default).
        - topK (optional): max results, default 10.

        Requires being logged in to open-moai (baseUrl + API key). Returns only documents the user is
        allowed to see (own private + orgs they belong to) — access is enforced server-side.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Search query" },
            "scope": { "type": "string", "enum": ["personal", "organization", "all"], "description": "Which documents to search (default: all)" },
            "topK": { "type": "integer", "description": "Max results (default 10)" }
          },
          "required": ["query"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("topK")] int? TopK);

    private sealed record DocResult(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("snippet")] string? Snippet,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("documentId")] string? DocumentId,
        [property: JsonPropertyName("score")] double? Score,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record DocSearchResponse(
        [property: JsonPropertyName("results")] List<DocResult>? Results,
        [property: JsonPropertyName("count")] int? Count);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Query))
        {
            yield return new ToolOutput("OrgDocs: 'query' is required", IsError: true);
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                L10n.Get("tools.orgDocs.notLoggedIn"),
                IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/knowledge/search";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        var tok = timeoutCts.Token;

        string? error = null;
        DocSearchResponse? data = null;
        try
        {
            data = await CallAsync(url, key, inp, tok).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = L10n.Get("tools.orgDocs.endpointMissing");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.orgDocs.timeout", Timeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.orgDocs.requestFailed", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var results = data?.Results ?? new List<DocResult>();
        if (results.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.orgDocs.noResults"));
            yield break;
        }

        var sb = new StringBuilder();
        var i = 1;
        foreach (var r in results)
        {
            var scope = string.IsNullOrWhiteSpace(r.Scope) ? "" : $"[{r.Scope}] ";
            var score = r.Score is { } s ? $" (score {s:0.00})" : "";
            sb.Append(i++).Append(". ").Append(scope).Append(r.Title ?? r.Source ?? "(untitled)").AppendLine(score);
            if (!string.IsNullOrWhiteSpace(r.Snippet))
            {
                sb.Append("   ").AppendLine(r.Snippet!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(r.Url))
            {
                sb.Append("   ").AppendLine(r.Url);
            }

            sb.AppendLine();
        }

        // 조직/개인 문서도 외부 콘텐츠 — 신뢰불가 경계로 감싸 주입(프롬프트 인젝션 방어).
        yield return new ToolOutput(Reminders.UntrustedToolOutput + sb.ToString().TrimEnd());
    }

    private static async Task<DocSearchResponse?> CallAsync(string url, string key, Input inp, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var scope = inp.Scope?.Trim().ToLowerInvariant();
        if (scope is not ("personal" or "organization" or "all"))
        {
            scope = "all";
        }

        var payload = new
        {
            query = inp.Query,
            scope,
            topK = inp.TopK is { } k ? Math.Clamp(k, 1, 50) : 10,
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

        return await resp.Content.ReadFromJsonAsync<DocSearchResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class EndpointMissingException : Exception
    {
    }
}
