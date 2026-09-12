using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace MoaiCode.Tools.Media;

/// <summary>
/// z.ai 비전 모델(glm-4.6v)로 이미지를 분석한다. 대화 모델과 같은 키·엔드포인트(ZaiEndpoint)를
/// 재사용하므로 별도 설정이 필요 없다 — 이미지는 data URL(base64)로 실어 보낸다.
/// 원격 URL 이미지는 z.ai 서버측 fetch가 불안정하므로(타임아웃/파싱 오류 관측됨) 직접 내려받아
/// base64 로 변환해 전송한다(다운로더·SSRF 가드는 ImageFetchTool 과 동일 규칙).
///
/// 예전엔 npx 기반 zai-vision MCP 서버를 썼는데 Windows(npx.cmd)에서 스폰이 안 되고 별도 키
/// 주입도 필요해서, WebSearch 를 Gemini → z.ai 내장으로 전환했던 것과 같은 이유로 내장 툴로 대체했다.
/// </summary>
public sealed class ImageAnalysisTool : ITool
{
    private const int MaxBytes = 20_000_000;
    private const int MaxRedirects = 5;
    // 짧은 변이 이보다 작으면 글자가 뭉개져 OCR 정확도가 급감한다(2026-09-12 234px UI 스크린샷 관측).
    private const int MinShortEdge = 512;
    private const int MaxUpscaleFactor = 3; // 아이콘급 이미지의 과대확대 방지
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(120);

    public string Name => "ImageAnalysis";

    public string Description => """
        Analyzes an image with a vision model and returns the answer to a question.

        Usage:
        - image_source (required): local file path OR http(s) URL of the image.
        - prompt (required): what to analyze/extract — be specific (text OCR, layout, colors, charts…).
        - Works on screenshots, photos, diagrams, UI mockups, and data visualizations.
        - For data visualizations, ask for trends/anomalies/comparisons to get structured insight.
        """;

    public bool IsReadOnly => true;

    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "image_source": { "type": "string", "description": "Local file path or http(s) URL of the image" },
            "prompt": { "type": "string", "description": "Question or instruction for the image analysis" }
          },
          "required": ["image_source", "prompt"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("image_source")] string? ImageSource,
        [property: JsonPropertyName("prompt")] string? Prompt);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.ImageSource))
        {
            yield return new ToolOutput("ImageAnalysis: 'image_source' is required", IsError: true);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(inp.Prompt))
        {
            yield return new ToolOutput("ImageAnalysis: 'prompt' is required", IsError: true);
            yield break;
        }

        var key = ZaiEndpoint.ApiKey();
        var baseUrl = ZaiEndpoint.BaseUrl();
        if (string.IsNullOrWhiteSpace(key))
        {
            yield return new ToolOutput(L10n.Get("tools.imageAnalysis.noKey"), IsError: true);
            yield break;
        }

        // 1) 이미지 확보: 로컬 파일이면 그대로, http(s) URL 이면 직접 내려받는다.
        byte[] bytes;
        string mediaType;
        string? loadError = null;
        try
        {
            (bytes, mediaType) = await AcquireImageAsync(inp.ImageSource!, context.WorkingDirectory, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            (bytes, mediaType) = (Array.Empty<byte>(), string.Empty);
            loadError = L10n.Get("tools.imageAnalysis.loadFailed", ex.Message);
        }

        if (loadError is not null)
        {
            yield return new ToolOutput(loadError, IsError: true);
            yield break;
        }

        if (bytes.Length == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.imageAnalysis.emptyImage"), IsError: true);
            yield break;
        }

        // 1.5) 작은 이미지는 자동 업스케일 — 비전 모델의 최소 가독 해상도 미만에서 글자가 뭉개진다.
        (bytes, mediaType) = EnsureMinEdge(bytes, mediaType);

        // 2) 비전 모델 호출(data URL base64).
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ApiTimeout);

        string? error = null;
        string answer = string.Empty;
        try
        {
            answer = await AskAsync(baseUrl!, key!, bytes, mediaType, inp.Prompt!, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = L10n.Get("tools.imageAnalysis.timeout", ApiTimeout.TotalSeconds);
        }
        catch (HttpRequestException ex)
        {
            error = L10n.Get("tools.imageAnalysis.requestFailed", ex.Message);
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        // 3) 이미지에서 추출한 텍스트(OCR 등)는 외부 콘텐츠 — 인젝션 경계를 붙인다.
        yield return new ToolOutput(Reminders.UntrustedToolOutput + answer);
    }

    private static async Task<(byte[] Bytes, string MediaType)> AcquireImageAsync(
        string source, string workingDir, CancellationToken ct)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await DownloadAsync(uri, ct).ConfigureAwait(false);
        }

        var path = Path.GetFullPath(Path.IsPathRooted(source) ? source : Path.Combine(workingDir, source));
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var mediaType = MediaForExtension(Path.GetExtension(path));
        return (bytes, mediaType);
    }

    private static async Task<string> AskAsync(
        string baseUrl, string key, byte[] bytes, string mediaType, string prompt, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var dataUrl = BuildDataUrl(mediaType, bytes);
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = ZaiEndpoint.VisionModel(),
            // 비전 호출은 답변 본문이 목적이라 reasoning 토큰은 끈다(지연·비용 절감, 검증됨).
            ["thinking"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = "disabled" },
            ["messages"] = new object[]
            {
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["type"] = "image_url",
                            // image_url 은 {"url": "..."} 객체 형태여야 한다(문자열이면 1214 format error).
                            ["image_url"] = new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["url"] = dataUrl,
                            },
                        },
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["type"] = "text",
                            ["text"] = prompt,
                        },
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

        return RenderAnswer(text);
    }

    /// <summary>응답에서 message.content 를 꺼낸다. reasoning_content 만 있고 content 가 비으면 안내.</summary>
    public static string RenderAnswer(string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return body;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                return string.Empty;
            }

            var msg = choices[0].TryGetProperty("message", out var m) ? m : default;
            if (msg.ValueKind == JsonValueKind.Object
                && msg.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    return s;
                }
            }

            // content 가 비는 경우: finish_reason=length 로 잘렸거나 reasoning 만 스트리밍된 경우.
            return "(empty answer — the vision model returned no content; try a shorter prompt)";
        }
    }

    /// <summary>MIME 타입과 base64 바이트로 data URL 을 만든다(테스트 대상).</summary>
    public static string BuildDataUrl(string mediaType, byte[] bytes)
        => $"data:{(string.IsNullOrEmpty(mediaType) ? "image/png" : mediaType)};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>
    /// 짧은 변이 <see cref="MinShortEdge"/> 미만인 래스터 이미지를 Lanczos 로 확대한다.
    /// 비전 모델은 낮은 해상도의 작은 글씨를 뭉개서 읽으므로 업스케일이 OCR 품질을 크게 올린다.
    /// 디코드 실패·애니메이션 GIF 등 처리 불가한 입력은 원본을 그대로 돌려준다(보조 경로이므로).
    /// </summary>
    public static (byte[] Bytes, string MediaType) EnsureMinEdge(byte[] bytes, string mediaType)
    {
        try
        {
            using var image = Image.Load(bytes);
            var shortEdge = Math.Min(image.Width, image.Height);
            var scale = Math.Min(MinShortEdge / (double)shortEdge, MaxUpscaleFactor);
            if (scale <= 1)
            {
                return (bytes, mediaType);
            }

            var newWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
            var newHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(ctx => ctx.Resize(newWidth, newHeight, KnownResamplers.Lanczos3));

            using var ms = new MemoryStream();
            image.SaveAsPng(ms); // 업스케일본은 무손실 PNG 로 재인코딩(손실 압축 반복 방지)
            return (ms.ToArray(), "image/png");
        }
        catch
        {
            return (bytes, mediaType);
        }
    }

    private static string MediaForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/png",
    };

    // 이하 다운로더는 ImageFetchTool 과 동일 규칙(리다이렉트 5회 + SSRF 가드 + 크기 상한).
    private static async Task<(byte[] Bytes, string MediaType)> DownloadAsync(Uri uri, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(DownloadTimeout);

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
            await GuardSsrfAsync(current, timeoutCts.Token).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);

            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                if (hop >= MaxRedirects)
                {
                    throw new HttpRequestException(L10n.Get("tools.imageFetch.redirectExceeded", MaxRedirects));
                }

                current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException(
                        L10n.Get("tools.imageFetch.redirectSchemeDenied", current.Scheme));
                }

                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }

            var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (media.Length > 0 && !media.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException(L10n.Get("tools.imageFetch.notImage", media));
            }

            var bytes = await ReadCappedAsync(resp, timeoutCts.Token).ConfigureAwait(false);
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
                throw new HttpRequestException(L10n.Get("tools.imageFetch.hostResolveFailed", uri.Host));
            }
        }

        foreach (var ip in addrs)
        {
            if (ip.IsIPv6LinkLocal)
            {
                throw new HttpRequestException(L10n.Get("tools.imageFetch.linkLocalBlocked", ip));
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254)
                {
                    throw new HttpRequestException(L10n.Get("tools.imageFetch.metadataBlocked", ip));
                }
            }
        }
    }
}
