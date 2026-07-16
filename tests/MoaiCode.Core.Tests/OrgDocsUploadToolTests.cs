using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Knowledge;
using Xunit;

namespace MoaiCode.Core.Tests;

// OrgDocsUpload(문서 업로드) 툴: POST {baseUrl}/knowledge?orgId= multipart 계약 (§4).
[Collection("EnvMutating")]
public sealed class OrgDocsUploadToolTests : IDisposable
{
    private readonly string _dir;

    public OrgDocsUploadToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-upload-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    private async Task<(string Text, bool Error)> RunAsync(object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in new OrgDocsUploadTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { sb.Append(o.Text); err |= o.IsError; }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public void Is_a_write_tool()
    {
        // 업로드는 외부 전송 — 쓰기로 분류되어 권한 게이트를 통과해야 한다.
        Assert.False(new OrgDocsUploadTool().IsReadOnly);
    }

    [Fact]
    public async Task Missing_file_reports_error()
    {
        var (text, err) = await RunAsync(new { orgId = "org1", path = "nope.docx" });
        Assert.True(err);
        Assert.Contains("파일이 없습니다", text);
    }

    [Fact]
    public async Task Uploads_multipart_with_orgId_and_visibility()
    {
        var file = Path.Combine(_dir, "report.docx");
        await File.WriteAllTextAsync(file, "hello");

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? gotPath = null, gotQuery = null, gotAuth = null, gotContentType = null, gotBody = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            gotPath = ctx.Request.Url?.AbsolutePath;
            gotQuery = ctx.Request.Url?.Query;
            gotAuth = ctx.Request.Headers["Authorization"];
            gotContentType = ctx.Request.ContentType;
            using (var sr = new StreamReader(ctx.Request.InputStream))
            {
                gotBody = await sr.ReadToEndAsync();
            }

            var bytes = Encoding.UTF8.GetBytes(
                """{"success":true,"visibility":"organization","documents":[{"id":123,"filename":"report.docx","status":"uploaded"}],"skipped":[]}""");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        });

        var (baseUrl, key) = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
                              Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", $"http://127.0.0.1:{port}");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-key");

            var (text, err) = await RunAsync(new { orgId = "org1", path = "report.docx", visibility = "organization" });
            await serverTask;

            Assert.False(err, text);
            Assert.Equal("/knowledge", gotPath);
            Assert.Contains("orgId=org1", gotQuery);
            Assert.Equal("Bearer sk-test-key", gotAuth);
            Assert.StartsWith("multipart/form-data", gotContentType);
            Assert.Contains("name=files", gotBody);          // .NET 은 따옴표 없이 출력(표준 허용)
            Assert.Contains("filename=report.docx", gotBody);
            Assert.Contains("name=visibility", gotBody);
            Assert.Contains("organization", gotBody);
            // 결과 렌더
            Assert.Contains("업로드됨", text);
            Assert.Contains("id=123", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
            listener.Stop();
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
