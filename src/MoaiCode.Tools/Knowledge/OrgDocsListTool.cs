using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// 조직 문서함의 업로드 문서 목록을 조회한다(브라우징). 의미검색은 OrgDocs, 목록은 이 툴.
/// 서버 계약상 orgId 가 필수다(open-moai/docs/moai-code-knowledge-api.md §2).
/// 계약: GET {baseUrl}/knowledge?orgId=&lt;id&gt;&amp;search=&lt;term&gt;.
/// </summary>
public sealed class OrgDocsListTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "OrgDocsList";

    public string Description => """
        Lists the uploaded documents in an organization's knowledge base: title, filename, type, size,
        visibility, processing status. This is BROWSING, not search — for semantic search use OrgDocs.
        orgId is optional: if omitted it is auto-resolved from your login (used automatically when you
        belong to exactly one organization; if several, you'll be asked to pick — see OrgList).
        Do NOT ask the user for orgId first. Optional `search` filters by title/filename.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "orgId": { "type": "string", "description": "Organization id (optional — auto-resolved from your login if omitted)" },
            "search": { "type": "string", "description": "Filter by title/filename substring (optional)" }
          }
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("orgId")] string? OrgId,
        [property: JsonPropertyName("search")] string? Search);

    private sealed record DocItem(
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("fileType")] string? FileType,
        [property: JsonPropertyName("fileSize")] long? FileSize,
        [property: JsonPropertyName("visibility")] string? Visibility,
        [property: JsonPropertyName("processingStatus")] string? ProcessingStatus,
        [property: JsonPropertyName("ragEnabled")] bool? RagEnabled);

    private sealed record ListResponse(
        [property: JsonPropertyName("organizationId")] string? OrganizationId,
        [property: JsonPropertyName("items")] List<DocItem>? Items,
        [property: JsonPropertyName("count")] int? Count);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                L10n.Get("tools.orgDocsList.notLoggedIn"), IsError: true);
            yield break;
        }

        var (orgId, orgErr) = await OrgResolver.ResolveAsync("OrgDocsList", baseUrl, key, inp?.OrgId, ct)
            .ConfigureAwait(false);
        if (orgErr is not null)
        {
            yield return new ToolOutput(orgErr, IsError: true);
            yield break;
        }

        var qs = "?orgId=" + HttpUtility.UrlEncode(orgId);
        if (!string.IsNullOrWhiteSpace(inp?.Search))
        {
            qs += "&search=" + HttpUtility.UrlEncode(inp!.Search);
        }

        var url = baseUrl.TrimEnd('/') + "/knowledge" + qs;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        ListResponse? data = null;
        try
        {
            data = await CallAsync(url, key, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = L10n.Get("tools.orgDocsList.endpointMissing");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.orgDocsList.timeout", Timeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.orgDocsList.requestFailed", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var items = data?.Items ?? new List<DocItem>();
        if (items.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.orgDocsList.noDocs"));
            yield break;
        }

        var sb = new StringBuilder();
        sb.AppendLine(L10n.Get("tools.orgDocsList.header", items.Count));
        sb.AppendLine();
        foreach (var d in items)
        {
            sb.Append("• [").Append(d.Id.ValueKind == JsonValueKind.Undefined ? "?" : d.Id.ToString()).Append("] ")
              .Append(d.Title ?? d.Filename ?? "(untitled)");
            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.Filename) && d.Filename != d.Title) meta.Add(d.Filename!);
            if (!string.IsNullOrWhiteSpace(d.Visibility)) meta.Add(d.Visibility!);
            if (d.FileSize is { } s) meta.Add(FormatSize(s));
            if (!string.IsNullOrWhiteSpace(d.ProcessingStatus) && d.ProcessingStatus != "completed")
                meta.Add(L10n.Get("tools.orgDocsList.processing", d.ProcessingStatus));
            if (d.RagEnabled == false) meta.Add("RAG off");
            if (meta.Count > 0) sb.Append("  (").Append(string.Join(" · ", meta)).Append(')');
            sb.AppendLine();
        }

        // 외부 데이터(제목/파일명) — 신뢰불가 경계로 감싼다.
        yield return new ToolOutput(Reminders.UntrustedToolOutput + sb.ToString().TrimEnd());
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.#}MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0.#}KB",
        _ => $"{bytes}B",
    };

    private static async Task<ListResponse?> CallAsync(string url, string key, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointMissingException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<ListResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class EndpointMissingException : Exception
    {
    }
}
