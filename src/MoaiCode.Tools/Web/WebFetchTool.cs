using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
        yield return new ToolOutput(header + body);
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
            var text = Encoding.UTF8.GetString(bytes);

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
