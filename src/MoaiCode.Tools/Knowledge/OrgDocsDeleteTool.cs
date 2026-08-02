using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// 조직 문서함에서 문서를 완전 삭제한다(벡터+청크+문서 row). 되돌릴 수 없는 쓰기 — 권한 게이트 통과.
/// 권한: 업로더 본인 / 조직 매니저 / 시스템 admin (서버에서 강제).
/// 계약: DELETE {baseUrl}/knowledge/{documentId}?orgId=&lt;id&gt;, moai-code-knowledge-api.md §6.
/// </summary>
public sealed class OrgDocsDeleteTool : ITool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "OrgDocsDelete";

    public string Description => """
        Permanently deletes a document from the organization's knowledge base (조직 문서함): removes the file, its chunks and
        vectors. Irreversible write action — asks for confirmation. Requires the documentId (from
        OrgDocsList). orgId is optional: if omitted it is auto-resolved from your login (used automatically
        when you belong to exactly one organization; if several, you'll be asked to pick — see OrgList).
        Only the uploader, an org manager, or a system admin may delete (enforced by the server).
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "documentId": { "type": "string", "description": "Document id (from OrgDocsList)" },
            "orgId": { "type": "string", "description": "Organization id (optional — auto-resolved from your login if omitted)" }
          },
          "required": ["documentId"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("documentId")] string? DocumentId,
        [property: JsonPropertyName("orgId")] string? OrgId);

    private sealed record DeleteResponse(
        [property: JsonPropertyName("success")] bool? Success,
        [property: JsonPropertyName("id")] JsonElement Id);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.DocumentId))
        {
            yield return new ToolOutput("OrgDocsDelete: 'documentId' 가 필요합니다.", IsError: true);
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                "OrgDocsDelete: open-moai 연결 정보가 없습니다. `moai login` 으로 로그인하세요.", IsError: true);
            yield break;
        }

        var (orgId, orgErr) = await OrgResolver.ResolveAsync("OrgDocsDelete", baseUrl, key, inp.OrgId, ct)
            .ConfigureAwait(false);
        if (orgErr is not null)
        {
            yield return new ToolOutput(orgErr, IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/knowledge/" + HttpUtility.UrlEncode(inp.DocumentId)
                  + "?orgId=" + HttpUtility.UrlEncode(orgId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        DeleteResponse? data = null;
        try
        {
            data = await CallAsync(url, key, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (NotFoundException)
        {
            error = $"OrgDocsDelete: 문서를 찾을 수 없습니다 (id={inp.DocumentId}). 이미 삭제됐거나 접근 불가.";
        }
        catch (ForbiddenException)
        {
            error = "OrgDocsDelete: 삭제 권한이 없습니다 — 본인이 업로드한 문서만 삭제할 수 있습니다 " +
                    "(조직 매니저/관리자 제외). 서버가 소유자를 검증합니다.";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = $"OrgDocsDelete: {Timeout.TotalSeconds:0}초 타임아웃";
        }
        catch (HttpRequestException ex)
        {
            error = $"OrgDocsDelete: 요청 실패 — {ex.Message}";
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var id = data?.Id.ValueKind is JsonValueKind.Undefined or null ? inp.DocumentId : data!.Id.ToString();
        yield return new ToolOutput($"OK: 문서 {id} 삭제됨 (벡터·청크 포함).");
    }

    private static async Task<DeleteResponse?> CallAsync(string url, string key, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        using var resp = await client.DeleteAsync(url, ct).ConfigureAwait(false);

        // 삭제는 404(엔드포인트 없음)와 404(문서 없음)를 구분하기 어렵다 — 서버가 문서없음도 404 반환.
        // 여기선 문서없음으로 안내(엔드포인트는 §6 로 배포됨을 전제).
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new NotFoundException();
        }

        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new ForbiddenException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<DeleteResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class NotFoundException : Exception
    {
    }

    private sealed class ForbiddenException : Exception
    {
    }
}
