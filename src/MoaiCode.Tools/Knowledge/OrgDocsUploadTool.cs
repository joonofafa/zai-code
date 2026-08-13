using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// 로컬 파일 또는 디렉토리를 조직 문서함에 업로드한다(쓰기 — 권한 게이트 통과). 파일 1개는 바로 올리고,
/// 디렉토리/여러 파일(배치)은 먼저 미리보기를 돌려주고 confirm=true 로 재호출해야 실제 업로드한다.
/// 계약: POST {baseUrl}/knowledge?orgId=&lt;id&gt; (multipart/form-data, files 다중), §4.
/// </summary>
public sealed class OrgDocsUploadTool : ITool
{
    private const long MaxFileBytes = 100L * 1024 * 1024; // 서버 상한 100MB
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    // 디렉토리 확장 시 올릴 문서형 확장자(지식베이스 임베딩 대상). 명시된 개별 파일은 이 필터를 적용하지 않는다.
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".pdf", ".txt", ".md", ".csv",
    };

    public string Name => "OrgDocsUpload";

    public string Description => """
        Uploads local file(s) to the organization's knowledge base, where they are embedded for later search.
        `path` may be a single file OR a directory; `paths` may list several files/directories. When a
        directory is given, only document-type files (.docx/.xlsx/.pptx/.pdf/.txt/.md/.csv) are picked up
        — top-level only unless `recursive: true`. Write action — asks for confirmation.
        Batch behaviour: when more than one file would be uploaded, the tool first returns a PREVIEW
        (file list + sizes, nothing uploaded); re-call the SAME arguments plus `confirm: true` to actually
        upload. A single file uploads directly. orgId is optional (auto-resolved from your login; see
        OrgList) — do NOT ask the user for it. Optional visibility (private|organization|company, default
        private) and categoryId.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "orgId": { "type": "string", "description": "Organization id (optional — auto-resolved from your login if omitted)" },
            "path": { "type": "string", "description": "Local file OR directory path (relative to workspace)" },
            "paths": { "type": "array", "items": { "type": "string" }, "description": "Several file/directory paths (optional, alternative to path)" },
            "recursive": { "type": "boolean", "description": "Recurse into subdirectories when a directory is given (default false)" },
            "confirm": { "type": "boolean", "description": "Set true to actually upload a batch after reviewing the preview (default false)" },
            "visibility": { "type": "string", "enum": ["private", "organization", "company"], "description": "Default private" },
            "categoryId": { "type": "integer", "description": "Organization category id (optional)" }
          }
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("orgId")] string? OrgId,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("paths")] List<string>? Paths,
        [property: JsonPropertyName("recursive")] bool? Recursive,
        [property: JsonPropertyName("confirm")] bool? Confirm,
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
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(inp?.Path))
        {
            roots.Add(inp!.Path!);
        }

        if (inp?.Paths is { Count: > 0 } ps)
        {
            roots.AddRange(ps.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        if (roots.Count == 0)
        {
            yield return new ToolOutput(
                L10n.Get("tools.orgDocsUpload.pathRequired"), IsError: true);
            yield break;
        }

        // 로컬에서 대상 파일을 해소(디렉토리는 문서형만). 서버는 아직 건드리지 않는다.
        var (sendable, oversized, notFound, resolveError) = ResolveTargets(roots, inp!.Recursive == true, context);
        if (resolveError is not null)
        {
            yield return new ToolOutput(resolveError, IsError: true);
            yield break;
        }

        if (sendable.Count == 0)
        {
            var sb = new StringBuilder(L10n.Get("tools.orgDocsUpload.noDocs"));
            if (notFound.Count > 0)
            {
                sb.Append(L10n.Get("tools.orgDocsUpload.missingFiles")).Append(string.Join(", ", notFound));
            }

            foreach (var (p, size) in oversized)
            {
                sb.Append(L10n.Get("tools.orgDocsUpload.skippedOversizeInline", RelativeTo(context, p), FormatSize(size)));
            }

            yield return new ToolOutput(sb.ToString(), IsError: true);
            yield break;
        }

        // 배치(2개 이상)인데 아직 확인 전이면 미리보기만 돌려준다(업로드 없음).
        if (sendable.Count > 1 && inp.Confirm != true)
        {
            yield return new ToolOutput(RenderPreview(sendable, oversized, notFound, inp.Visibility, context));
            yield break;
        }

        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(
                L10n.Get("tools.orgDocsUpload.notLoggedIn"), IsError: true);
            yield break;
        }

        var (orgId, orgErr) = await OrgResolver.ResolveAsync("OrgDocsUpload", baseUrl, key, inp.OrgId, ct)
            .ConfigureAwait(false);
        if (orgErr is not null)
        {
            yield return new ToolOutput(orgErr, IsError: true);
            yield break;
        }

        var url = baseUrl.TrimEnd('/') + "/knowledge?orgId=" + HttpUtility.UrlEncode(orgId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        UploadResponse? data = null;
        try
        {
            data = await CallAsync(url, key, sendable, inp.Visibility, inp.CategoryId, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            error = L10n.Get("tools.orgDocsUpload.endpointMissing");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.orgDocsUpload.timeout", Timeout.TotalMinutes);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.orgDocsUpload.requestFailed", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        yield return new ToolOutput(Render(data, sendable.Count, oversized, notFound, context));
    }

    // roots 를 실제 업로드 대상 파일 목록으로 해소한다. 디렉토리는 문서형만, 개별 파일은 그대로.
    private static (List<string> Sendable, List<(string Path, long Size)> Oversized, List<string> NotFound, string? Error)
        ResolveTargets(List<string> roots, bool recursive, ToolContext context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sendable = new List<string>();
        var oversized = new List<(string, long)>();
        var notFound = new List<string>();

        void Consider(string full)
        {
            if (!seen.Add(full))
            {
                return;
            }

            var size = new FileInfo(full).Length;
            if (size > MaxFileBytes)
            {
                oversized.Add((full, size));
            }
            else
            {
                sendable.Add(full);
            }
        }

        foreach (var root in roots)
        {
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(root)
                    ? root
                    : System.IO.Path.Combine(context.WorkingDirectory, root));
            }
            catch (Exception ex)
            {
                return (sendable, oversized, notFound, L10n.Get("tools.orgDocsUpload.badPath", root, ex.Message));
            }

            if (Directory.Exists(full))
            {
                var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                foreach (var f in Directory.EnumerateFiles(full, "*", opt).OrderBy(f => f, StringComparer.Ordinal))
                {
                    var name = System.IO.Path.GetFileName(f);
                    if (name.StartsWith('.') || !DocExtensions.Contains(System.IO.Path.GetExtension(f)))
                    {
                        continue; // 숨김 파일 · 비문서형 제외
                    }

                    Consider(f);
                }
            }
            else if (File.Exists(full))
            {
                Consider(full); // 명시된 개별 파일은 확장자 필터 없이 업로드
            }
            else
            {
                notFound.Add(root);
            }
        }

        return (sendable, oversized, notFound, null);
    }

    private static string RenderPreview(
        List<string> sendable, List<(string Path, long Size)> oversized, List<string> notFound,
        string? visibility, ToolContext context)
    {
        var total = sendable.Sum(p => new FileInfo(p).Length);
        var vis = NormalizeVisibility(visibility) ?? "private";
        var sb = new StringBuilder();
        sb.AppendLine(L10n.Get("tools.orgDocsUpload.previewHeader", sendable.Count, FormatSize(total), vis));
        var i = 1;
        foreach (var p in sendable)
        {
            sb.Append("  ").Append(i++.ToString("00")).Append(". ").Append(RelativeTo(context, p))
              .Append("  (").Append(FormatSize(new FileInfo(p).Length)).Append(')').AppendLine();
        }

        foreach (var (p, size) in oversized)
        {
            sb.AppendLine(L10n.Get("tools.orgDocsUpload.previewSkippedOversize", RelativeTo(context, p), FormatSize(size)));
        }

        if (notFound.Count > 0)
        {
            sb.AppendLine(L10n.Get("tools.orgDocsUpload.notFoundLine", string.Join(", ", notFound)));
        }

        sb.Append(L10n.Get("tools.orgDocsUpload.confirmHint"));
        return sb.ToString();
    }

    private static string Render(
        UploadResponse? data, int attempted, List<(string Path, long Size)> oversized,
        List<string> notFound, ToolContext context)
    {
        var docs = data?.Documents ?? new List<UploadedDoc>();
        var skipped = data?.Skipped ?? new List<SkippedDoc>();

        var sb = new StringBuilder();
        if (docs.Count == 0 && skipped.Count > 0)
        {
            var s = skipped[0];
            sb.Append(L10n.Get("tools.orgDocsUpload.rejected", s.Filename, s.Reason));
            return sb.ToString();
        }

        sb.AppendLine(L10n.Get("tools.orgDocsUpload.completedHeader", docs.Count, attempted));
        foreach (var d in docs)
        {
            sb.Append("  • '").Append(d.Filename).Append("' (id=")
              .Append(d.Id.ValueKind == JsonValueKind.Undefined ? "?" : d.Id.ToString())
              .Append(L10n.Get("tools.orgDocsUpload.statusLabel")).Append(d.Status ?? "uploaded").Append(')');
            if (!string.IsNullOrWhiteSpace(data?.Visibility))
            {
                sb.Append(L10n.Get("tools.orgDocsUpload.visibilityLabel")).Append(data!.Visibility);
            }

            sb.AppendLine();
        }

        foreach (var s in skipped)
        {
            sb.AppendLine(L10n.Get("tools.orgDocsUpload.skippedLine", s.Filename, s.Reason));
        }

        foreach (var (p, size) in oversized)
        {
            sb.AppendLine(L10n.Get("tools.orgDocsUpload.skippedOversizeLine", RelativeTo(context, p), FormatSize(size)));
        }

        if (notFound.Count > 0)
        {
            sb.AppendLine(L10n.Get("tools.orgDocsUpload.notFoundLine", string.Join(", ", notFound)));
        }

        sb.Append(L10n.Get("tools.orgDocsUpload.asyncNote"));
        return sb.ToString();
    }

    private static async Task<UploadResponse?> CallAsync(
        string url, string key, IReadOnlyList<string> filePaths, string? visibility, int? categoryId,
        CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // undici(Next.js request.formData())는 RFC 7578 대로 파싱한다:
        //  - 파트 name/filename 은 따옴표로 감싸야 한다(.NET 기본은 무따옴표 → 파싱 실패).
        //  - Content-Type 의 boundary 에 따옴표가 있어도 실패 → 특수문자 없는 boundary 를 직접 지정.
        // 그래서 파트 ContentDisposition 을 직접 설정해 따옴표를 강제하고 filename* 확장은 생략한다.
        var boundary = "MoaiBoundary" + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent(boundary);

        foreach (var filePath in filePaths)
        {
            var bytes = await File.ReadAllBytesAsync(filePath, ct).ConfigureAwait(false);
            var fileContent = new ByteArrayContent(bytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(ContentTypeFor(filePath));
            fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
            {
                Name = "\"files\"",  // 서버는 getAll('files') — 다중 파일 동일 name
                FileName = "\"" + System.IO.Path.GetFileName(filePath) + "\"",
            };
            form.Add(fileContent);
        }

        var vis = NormalizeVisibility(visibility);
        if (vis is not null)
        {
            AddField(form, "visibility", vis);
        }

        if (categoryId is { } cat && cat > 0)
        {
            AddField(form, "categoryId", cat.ToString());
        }

        // 따옴표 없는 boundary 로 Content-Type 재설정.
        form.Headers.Remove("Content-Type");
        form.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

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

    // 파트 name 을 따옴표로 감싼 form-data 필드 추가(undici 파싱 호환).
    private static void AddField(MultipartFormDataContent form, string name, string value)
    {
        var sc = new StringContent(value);
        sc.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"" + name + "\"",
        };
        form.Add(sc);
    }

    private static string? NormalizeVisibility(string? v)
    {
        var vis = v?.Trim().ToLowerInvariant();
        return vis is "private" or "organization" or "company" ? vis : null;
    }

    private static string RelativeTo(ToolContext context, string full)
    {
        try
        {
            var rel = System.IO.Path.GetRelativePath(context.WorkingDirectory, full);
            return rel.StartsWith("..", StringComparison.Ordinal) ? full : rel;
        }
        catch
        {
            return System.IO.Path.GetFileName(full);
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.#}MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0.#}KB",
        _ => $"{bytes}B",
    };

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
