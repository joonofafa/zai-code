using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Web;

/// <summary>
/// URL을 가져와 본문 텍스트(HTML은 태그 제거 후 가독 텍스트)로 반환. API 키 불필요.
/// 안전장치: http/https만, 30초 타임아웃, 5MB 응답 상한, 리다이렉트 5회 제한 + 매 홉 재검증,
/// 클라우드 메타데이터/링크로컬 IP(169.254.x, IPv6 link-local) 차단(SSRF 방지). 프록시는 자동 적용.
/// </summary>
public sealed class WebFetchTool : ITool
{
    private const int MaxBytes = 5_000_000;
    private const int DefaultMaxLength = 50_000;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    static WebFetchTool()
    {
        // .NET Core 는 UTF-*/ASCII/Latin1 만 내장한다. EUC-KR·CP949·Shift-JIS 등을 쓰려면 등록 필요.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public string Name => "WebFetch";

    public string Description => """
        Fetches a URL over HTTP(S) and returns its content as readable text (HTML is stripped to text).

        Usage:
        - Provide an absolute http:// or https:// URL.
        - Use this to read documentation, API references, or pages. No API key required.
        - Output is truncated to a maximum length; set max_length to adjust.
        - For local file content use Read; for searching the codebase use Grep/Glob.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = ToolSchema.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": { "type": "string", "description": "Absolute http(s) URL" },
            "max_length": { "type": "integer", "description": "Max characters to return (default 50000)" }
          },
          "required": ["url"]
        }
        """);

    private sealed record Input(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("max_length")] int? MaxLength);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Url))
        {
            yield return new ToolOutput("WebFetch: 'url' is required", IsError: true);
            yield break;
        }

        if (!Uri.TryCreate(inp.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ToolOutput($"WebFetch: http(s) URL 만 허용됩니다: {inp.Url}", IsError: true);
            yield break;
        }

        var maxLength = inp.MaxLength is > 0 ? inp.MaxLength.Value : DefaultMaxLength;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);
        var tok = timeoutCts.Token;

        string? error = null;
        string body = string.Empty;
        string finalUrl = uri.ToString();
        var statusLine = string.Empty;

        try
        {
            (body, finalUrl, statusLine) = await FetchAsync(uri, maxLength, tok).ConfigureAwait(false);
        }
        catch (SsrfBlockedException ex)
        {
            error = $"WebFetch 거부 — {ex.Message}";
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = $"WebFetch: {Timeout.TotalSeconds:0}초 타임아웃 — {uri}";
        }
        catch (HttpRequestException ex)
        {
            error = $"WebFetch: 요청 실패 — {ex.Message}";
        }

        if (error is not null)
        {
            yield return new ToolOutput(error, IsError: true);
            yield break;
        }

        var header = finalUrl == uri.ToString()
            ? $"# {finalUrl}\n{statusLine}\n\n"
            : $"# {finalUrl} (redirected from {uri})\n{statusLine}\n\n";
        // 웹 콘텐츠는 신뢰불가 — 간접 프롬프트 인젝션 경계를 앞에 붙인다.
        yield return new ToolOutput(Reminders.UntrustedToolOutput + header + body);
    }

    private static async Task<(string Body, string FinalUrl, string Status)> FetchAsync(
        Uri uri, int maxLength, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // 리다이렉트마다 SSRF 재검증을 위해 수동 처리
            AutomaticDecompression = DecompressionMethods.All,
            // Proxy 미지정 → HttpClient.DefaultProxy(사내망 프록시 설정) 자동 사용
        };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MoAI-Code/0.1 (+webfetch)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain,application/json;q=0.9,*/*;q=0.8");

        var current = uri;
        for (var hop = 0; ; hop++)
        {
            await GuardSsrfAsync(current, ct).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            using var resp = await client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // 리다이렉트 수동 추적.
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc)
            {
                if (hop >= MaxRedirects)
                {
                    throw new HttpRequestException($"리다이렉트 {MaxRedirects}회 초과");
                }

                current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
                if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                {
                    throw new SsrfBlockedException($"리다이렉트 대상 스킴 비허용: {current.Scheme}");
                }

                continue;
            }

            var status = $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} · {resp.Content.Headers.ContentType?.MediaType ?? "?"}";
            var bytes = await ReadCappedAsync(resp, ct).ConfigureAwait(false);
            var media = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;

            // 바이너리(PDF/이미지 등)를 텍스트로 디코딩하면 쓰레기 문자열이 컨텍스트를 오염시킨다.
            if (!IsTextual(media))
            {
                return ($"(binary content: {media}, {bytes.Length} bytes — 텍스트로 표시하지 않음)",
                    current.ToString(), status);
            }

            // charset 은 Content-Type → HTML meta → BOM 순으로 판별 (한국 사이트의 EUC-KR/CP949 대응).
            var text = DecodeText(bytes, resp.Content.Headers.ContentType?.CharSet, media);

            if (media.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                text = HtmlToText(text);
            }

            if (text.Length > maxLength)
            {
                text = text[..maxLength] + $"\n… (truncated at {maxLength} chars)";
            }

            return (text, current.ToString(), status);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while (ms.Length < MaxBytes &&
               (n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, n);
        }

        return ms.ToArray();
    }

    // 텍스트로 취급할 미디어 타입. 빈 문자열(Content-Type 없음)은 기존 동작대로 텍스트로 간주.
    private static bool IsTextual(string media)
    {
        if (string.IsNullOrEmpty(media))
        {
            return true;
        }

        if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return media.Contains("json", StringComparison.OrdinalIgnoreCase)
            || media.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || media.Contains("javascript", StringComparison.OrdinalIgnoreCase)
            || media.Contains("x-yaml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BOM → Content-Type charset → HTML meta charset → UTF-8 순으로 인코딩을 판별해 디코딩한다.
    /// </summary>
    public static string DecodeText(byte[] bytes, string? charset, string media)
    {
        // 1) BOM 이 있으면 헤더보다 우선(BOM 은 실제 바이트가 말하는 사실).
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        // 2) Content-Type: text/html; charset=euc-kr
        var enc = TryGetEncoding(charset);

        // 3) 헤더에 없으면 문서 앞부분의 <meta charset=…> 를 훑는다(한국 사이트가 흔히 이 경우).
        if (enc is null && media.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var head = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            var m = MetaCharsetRegex.Match(head);
            if (m.Success)
            {
                enc = TryGetEncoding(m.Groups[1].Value);
            }
        }

        return (enc ?? Encoding.UTF8).GetString(bytes);
    }

    private static readonly Regex MetaCharsetRegex = new(
        """<meta[^>]+charset\s*=\s*["']?\s*([A-Za-z0-9_\-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Encoding? TryGetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(name.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            return null; // 알 수 없는 charset → 호출부에서 UTF-8 로 폴백
        }
    }

    // SSRF 가드: 호스트가 링크로컬/클라우드 메타데이터(169.254.x, IPv6 link-local)로 해석되면 차단.
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
                throw new SsrfBlockedException($"호스트 해석 실패: {uri.Host}");
            }
        }

        foreach (var ip in addrs)
        {
            if (IsLinkLocalOrMetadata(ip))
            {
                throw new SsrfBlockedException($"링크로컬/메타데이터 IP 접근 차단({ip})");
            }
        }
    }

    private static bool IsLinkLocalOrMetadata(IPAddress ip)
    {
        if (ip.IsIPv6LinkLocal)
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 169 && b[1] == 254; // 169.254.0.0/16 (AWS/GCP/Azure 메타데이터 포함)
        }

        return false;
    }

    // 의존성 없는 HTML→텍스트: script/style/주석 제거 → 블록 태그를 개행으로 → 잔여 태그 제거 → 엔티티 디코드.
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<script\b[^>]*>.*?</script\s*>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<style\b[^>]*>.*?</style\s*>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<!--.*?-->", " ", RegexOptions.Singleline);
        s = Regex.Replace(s, @"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", string.Empty); // 잔여 태그 제거
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    private sealed class SsrfBlockedException : Exception
    {
        public SsrfBlockedException(string message) : base(message) { }
    }
}
