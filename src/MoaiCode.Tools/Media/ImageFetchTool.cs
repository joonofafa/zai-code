using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Media;

/// <summary>
/// 웹의 이미지 URL 을 내려받아 로컬 파일로 저장한다(WebSearch 로 찾은 이미지 삽입용).
/// WebFetch 는 이미지를 바이너리로 보고 저장하지 않으므로 별도 툴로 분리.
/// 저장된 파일은 문서 생성(DocxCreate/PptxCreate 의 image 필드)이나
/// 열린 문서 편집(WordEdit/ExcelEdit/PowerPointEdit 의 insert_picture)으로 삽입할 수 있다.
/// 안전장치: http/https 만, 30초 타임아웃, 20MB 상한, 리다이렉트 5회 + 매 홉 SSRF 재검증,
/// content-type 이 image/* 인지 확인.
/// </summary>
public sealed class ImageFetchTool : ITool
{
    private const int MaxBytes = 20_000_000;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public string Name => "ImageFetch";

    public string Description => """
        Downloads an image from an http(s) URL and saves it as a local file.
        Input: url (required — a direct image URL, e.g. from WebSearch results), path (output file,
        relative to workspace; extension is inferred from the response if omitted).
        Use this to fetch a web image, then embed it via DocxCreate/PptxCreate (image field) or insert it
        into an open document via WordEdit/ExcelEdit/PowerPointEdit (insert_picture). Images only.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Absolute http(s) URL of an image" },
            "path": { "type": "string", "description": "Output file path (relative to workspace)" }
          },
          "required": ["url", "path"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("path")] string? Path);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Url))
        {
            yield return new ToolOutput("ImageFetch: 'url' is required", IsError: true);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("ImageFetch: 'path' is required", IsError: true);
            yield break;
        }

        if (!Uri.TryCreate(inp.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ToolOutput($"ImageFetch: http(s) URL 만 허용됩니다: {inp.Url}", IsError: true);
            yield break;
        }

        yield return new ToolStatus($"이미지 다운로드 중: {uri}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        string? error = null;
        byte[]? bytes = null;
        string mediaType = string.Empty;
        try
        {
            (bytes, mediaType) = await DownloadAsync(uri, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = $"ImageFetch: {Timeout.TotalSeconds:0}초 타임아웃";
        }
        catch (HttpRequestException ex)
        {
            error = "ImageFetch: 요청 실패 — " + ex.Message;
        }
        catch (Exception ex)
        {
            error = "ImageFetch: 오류 — " + ex.Message;
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        if (bytes is null || bytes.Length == 0)
        {
            yield return new ToolOutput("ImageFetch: 빈 응답입니다.", IsError: true);
            yield break;
        }

        if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            yield return new ToolOutput(
                $"ImageFetch: 이미지가 아닙니다(content-type={(string.IsNullOrEmpty(mediaType) ? "?" : mediaType)}).",
                IsError: true);
            yield break;
        }

        string outPath;
        string? writeErr = null;
        try
        {
            outPath = ResolveOutPath(inp.Path!, mediaType, context.WorkingDirectory);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllBytesAsync(outPath, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            outPath = string.Empty;
            writeErr = "ImageFetch: 저장 실패 — " + ex.Message;
        }

        if (writeErr is not null)
        {
            yield return new ToolOutput(writeErr, IsError: true);
            yield break;
        }

        var kb = bytes.Length / 1024.0;
        yield return new ToolOutput($"이미지 저장됨: {outPath} ({kb:0}KB, {mediaType})");
    }

    // 확장자가 없거나 이미지 확장자가 아니면 content-type 에 맞춰 붙인다.
    private static string ResolveOutPath(string path, string mediaType, string workingDir)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext) || !IsImageExt(ext))
        {
            path += ExtFor(mediaType);
        }

        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workingDir, path));
    }

    private static bool IsImageExt(string ext) => ext.ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff";

    private static string ExtFor(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        "image/tiff" => ".tiff",
        _ => ".png",
    };

    private static async Task<(byte[] Bytes, string MediaType)> DownloadAsync(Uri uri, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // 리다이렉트마다 SSRF 재검증
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MoAI-Code/0.1 (+imagefetch)");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/*,*/*;q=0.8");

        var current = uri;
        for (var hop = 0; ; hop++)
        {
            await GuardSsrfAsync(current, ct).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                if (hop >= MaxRedirects)
                {
                    throw new HttpRequestException($"리다이렉트 {MaxRedirects}회 초과");
                }

                current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException($"리다이렉트 대상 스킴 비허용: {current.Scheme}");
                }

                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            var bytes = await ReadCappedAsync(resp, ct).ConfigureAwait(false);
            return (bytes, media);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while (ms.Length < MaxBytes
               && (n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
        }

        return ms.ToArray();
    }

    // SSRF 가드: 호스트가 링크로컬/클라우드 메타데이터(169.254.x, IPv6 link-local)면 차단.
    private static async Task GuardSsrfAsync(Uri uri, CancellationToken ct)
    {
        IPAddress[] addrs;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addrs = new[] { literal };
        }
        else
        {
            try
            {
                addrs = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            }
            catch
            {
                throw new HttpRequestException($"호스트 해석 실패: {uri.Host}");
            }
        }

        foreach (var ip in addrs)
        {
            if (ip.IsIPv6LinkLocal)
            {
                throw new HttpRequestException($"링크로컬 IP 접근 차단({ip})");
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                {
                    throw new HttpRequestException($"메타데이터 IP 접근 차단({ip})");
                }
            }
        }
    }
}
