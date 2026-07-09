using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 로컬 HTTP 서버를 띄워 WebFetch 툴 경로 전체(charset 판별 → 디코딩 → HTML→텍스트, 바이너리 가드)를 검증.
/// </summary>
public sealed class WebFetchToolIntegrationTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly CancellationTokenSource _cts = new();

    public WebFetchToolIntegrationTests()
    {
        var port = FreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Close();
        }
        catch
        {
            // best-effort
        }

        _cts.Dispose();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                return; // listener 종료
            }

            var path = ctx.Request.Url!.AbsolutePath;
            byte[] body;
            string contentType;

            switch (path)
            {
                case "/header-euckr":
                    // Content-Type 헤더로 charset 을 알려주는 경우.
                    contentType = "text/html; charset=euc-kr";
                    body = EucKr("<html><body><p>안녕하세요 세계</p></body></html>");
                    break;

                case "/meta-euckr":
                    // 헤더엔 charset 이 없고 <meta> 로만 선언된 경우 (한국 레거시 사이트에서 흔함).
                    contentType = "text/html";
                    body = EucKr("<html><head><meta charset=\"euc-kr\"></head><body><p>안녕하세요 세계</p></body></html>");
                    break;

                case "/pdf":
                    contentType = "application/pdf";
                    body = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x00, 0xFF, 0xFE, 0x01 };
                    break;

                default:
                    contentType = "text/plain; charset=utf-8";
                    body = Encoding.UTF8.GetBytes("ok");
                    break;
            }

            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    private static byte[] EucKr(string s)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("euc-kr").GetBytes(s);
    }

    private async Task<string> FetchAsync(string path)
    {
        var tool = new WebFetchTool();
        using var doc = JsonDocument.Parse($$"""{"url":"{{_prefix.TrimEnd('/')}}{{path}}"}""");
        var outputs = new List<string>();
        await foreach (var p in tool.ExecuteAsync(
            doc.RootElement, new ToolContext(Path.GetTempPath(), PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                Assert.False(o.IsError, o.Text);
                outputs.Add(o.Text);
            }
        }

        return string.Join("\n", outputs);
    }

    [Fact]
    public async Task Decodes_euckr_from_content_type_header()
    {
        var text = await FetchAsync("/header-euckr");
        Assert.Contains("안녕하세요 세계", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decodes_euckr_from_meta_tag_when_header_lacks_charset()
    {
        var text = await FetchAsync("/meta-euckr");
        Assert.Contains("안녕하세요 세계", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_not_dump_binary_content_as_text()
    {
        var text = await FetchAsync("/pdf");
        Assert.Contains("binary content", text, StringComparison.Ordinal);
        Assert.Contains("application/pdf", text, StringComparison.Ordinal);
        Assert.DoesNotContain("�", text, StringComparison.Ordinal); // U+FFFD 치환문자 = 깨진 디코딩
    }
}
