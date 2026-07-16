using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// 로컬 파일을 조직 문서함에 업로드한다(쓰기 — 권한 게이트 통과). DocxCreate 등으로 만든 문서를
/// 바로 문서함에 올리는 흐름. 계약: POST {baseUrl}/knowledge?orgId=&lt;id&gt; (multipart/form-data),
/// open-moai/docs/moai-code-knowledge-api.md §4.
/// </summary>
public sealed class OrgDocsUploadTool : ITool
{
    private const long MaxFileBytes = 100L * 1024 * 1024; // 서버 상한 100MB
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public string Name => "OrgDocsUpload";

    public string Description => """
        Uploads a local file (e.g. a document made with DocxCreate/XlsxCreate/PptxCreate, or a pdf)
        to the organization's 문서함, where it is embedded for later search. Write action — asks for
        confirmation. Requires orgId (see OrgDocsList/OrgDocs for how to obtain it) and a local file path.
        Optional visibility (private|organization|company, default private) and categoryId.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "orgId": { "type": "string", "description": "Organization id (required)" },
            "path": { "type": "string", "description": "Local file path (relative to workspace)" },
            "visibility": { "type": "string", "enum": ["private", "organization", "company"], "description": "Default private" },
            "categoryId": { "type": "integer", "description": "Organization category id (optional)" }
          },
          "required": ["orgId", "path"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("orgId")] string? OrgId,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("visibility")] string? Visibility,
        [property: JsonPropertyName("categoryId")] int? CategoryId);

    private sealed record UploadedDoc(
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("status")] string? Status);

    private sealed record SkippedDoc(
        [property: JsonPropertyName("filename")] string? Filename,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record UploadResponse(
        [property: JsonPropertyName("success")] bool? Success,
        [property: JsonPropertyName("visibility")] string? Visibility,
        [property: JsonPropertyName("documents")] List<UploadedDoc>? Documents,
        [property: JsonPropertyName("skipped")] List<SkippedDoc>? Skipped);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.OrgId) || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("OrgDocsUpload: 'orgId' 와 'path' 가 필요합니다.", IsError: true);
            yield break;
        }

        string? full = null;
        string? pathError = null;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(inp.Path)
                ? inp.Path
                : System.IO.Path.Combine(context.WorkingDirectory, inp.Path));
        }
        catch (Exception ex)
        {
            pathError = ex.Message;
        }

        if (pathError is not null || full is null)
        {
            yield return new ToolOutput($"OrgDocsUpload: 잘못된 경로 — {pathError}", IsError: true);
            yield break;
        }

        if (!File.Exists(full))
        {
            yield return new ToolOutput($"OrgDocsUpload: 파일이 없습니다: {inp.Path}", IsError: true);
            yield break;
        }

        var size = new FileInfo(full).Length;
        if (size > MaxFileBytes)
        {
            yield return new ToolOutput(
                $"OrgDocsUpload: 파일이 너무 큽니다 ({size / (1024 * 1024)}MB > 100MB 상한).", IsError: true);
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                "OrgDocsUpload: open-moai 연결 정보가 없습니다. `moai login` 으로 로그인하세요.", IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/knowledge?orgId=" + HttpUtility.UrlEncode(inp.OrgId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        UploadResponse? data = null;
        try
        {
            data = await CallAsync(url, key, full, inp, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = "OrgDocsUpload: 이 서버에 업로드 엔드포인트(/api/v1/knowledge)가 없습니다. " +
                    "open-moai 측 배포가 필요합니다.";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = $"OrgDocsUpload: {Timeout.TotalMinutes:0}분 타임아웃";
        }
        catch (HttpRequestException ex)
        {
            error = $"OrgDocsUpload: 요청 실패 — {ex.Message}";
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        yield return new ToolOutput(Render(data, System.IO.Path.GetFileName(full)));
    }

    private static string Render(UploadResponse? data, string filename)
    {
        var docs = data?.Documents ?? new List<UploadedDoc>();
        var skipped = data?.Skipped ?? new List<SkippedDoc>();

        if (docs.Count == 0 && skipped.Count > 0)
        {
            var s = skipped[0];
            return $"OrgDocsUpload: 업로드 거부됨 — {s.Filename}: {s.Reason}";
        }

        var sb = new StringBuilder();
        foreach (var d in docs)
        {
            sb.Append("OK: '").Append(d.Filename ?? filename).Append("' 업로드됨 (id=")
              .Append(d.Id.ValueKind == JsonValueKind.Undefined ? "?" : d.Id.ToString())
              .Append(", 상태=").Append(d.Status ?? "uploaded").Append(").");
            if (!string.IsNullOrWhiteSpace(data?.Visibility))
            {
                sb.Append(" 공개범위=").Append(data!.Visibility);
            }

            sb.AppendLine();
        }

        sb.Append("문서함 임베딩은 서버가 비동기 처리합니다(검색 반영까지 잠시 소요). ");
        sb.Append("OrgDocsList 로 처리 상태를 확인할 수 있습니다.");
        foreach (var s in skipped)
        {
            sb.AppendLine().Append("건너뜀: ").Append(s.Filename).Append(" — ").Append(s.Reason);
        }

        return sb.ToString().TrimEnd();
    }

    private static async Task<UploadResponse?> CallAsync(
        string url, string key, string filePath, Input inp, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var form = new MultipartFormDataContent();
        var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(filePath));
        form.Add(fileContent, "files", System.IO.Path.GetFileName(filePath));

        var vis = inp.Visibility?.Trim().ToLowerInvariant();
        if (vis is "private" or "organization" or "company")
        {
            form.Add(new StringContent(vis), "visibility");
        }

        if (inp.CategoryId is { } cat && cat > 0)
        {
            form.Add(new StringContent(cat.ToString()), "categoryId");
        }

        using var resp = await client.PostAsync(url, form, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointMissingException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        return await resp.Content.ReadFromJsonAsync<UploadResponse>(cancellationToken: ct).ConfigureAwait(false);
    }

    private static string ContentTypeFor(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            _ => "application/octet-stream",
        };

    private sealed class EndpointMissingException : Exception
    {
    }
}
