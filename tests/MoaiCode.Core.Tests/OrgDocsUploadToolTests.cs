using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
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
        Assert.Contains(L10n.Get("tools.orgDocsUpload.missingFiles"), text);
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
            Assert.Contains("name=\"files\"", gotBody);          // undici 호환 위해 따옴표 강제
            Assert.Contains("filename=\"report.docx\"", gotBody);
            Assert.DoesNotContain("filename*", gotBody);         // 확장 파라미터 없어야(undici 파싱)
            Assert.Contains("name=\"visibility\"", gotBody);
            Assert.Contains("organization", gotBody);
            Assert.Contains("boundary=MoaiBoundary", gotContentType); // 무따옴표 boundary
            // 결과 렌더
            Assert.Contains(L10n.Get("tools.orgDocsUpload.completedHeader", 1, 1), text);
            Assert.Contains("id=123", text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
            listener.Stop();
        }
    }

    [Fact]
    public async Task Directory_batch_filters_docs_and_shows_preview()
    {
        // 최상위: a.docx + b.txt(문서형) = 2개, c.png(비문서형)·sub/d.pdf(하위)는 제외 → 미리보기(업로드 없음).
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.docx"), "x");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(_dir, "c.png"), "x");
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sub.FullName, "d.pdf"), "x");

        var (text, err) = await RunAsync(new { path = "." }); // 서버/env 불필요 — 미리보기는 로컬
        Assert.False(err, text);
        // 미리보기 헤더(문서 2개, 총 2B, 기본 공개범위 private)를 언어무관으로 검증.
        Assert.Contains(L10n.Get("tools.orgDocsUpload.previewHeader", 2, "2B", "private"), text);
        Assert.Contains("02.", text); // 두 번째 문서까지 번호 매겨 나열(개수=2)
        Assert.Contains("a.docx", text);
        Assert.Contains("b.txt", text);
        Assert.DoesNotContain("c.png", text); // 비문서형 제외
        Assert.DoesNotContain("d.pdf", text); // 하위폴더는 recursive 없이는 제외
        Assert.Contains("confirm:true", text);
    }

    [Fact]
    public async Task Recursive_includes_subdirectories_with_relative_paths()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.docx"), "x");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "x");
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sub.FullName, "d.pdf"), "x");

        var (text, err) = await RunAsync(new { path = ".", recursive = true });
        Assert.False(err, text);
        Assert.Contains(L10n.Get("tools.orgDocsUpload.previewHeader", 3, "3B", "private"), text);
        Assert.Contains(Path.Combine("sub", "d.pdf"), text); // 상대경로로 서브폴더 노출
    }

    [Fact]
    public async Task Confirmed_batch_uploads_all_files_in_one_request()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.docx"), "aaa");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "bbb");

        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var requestCount = 0;
        var filesParts = -1;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            requestCount++;
            string body;
            using (var sr = new StreamReader(ctx.Request.InputStream)) { body = await sr.ReadToEndAsync(); }
            filesParts = System.Text.RegularExpressions.Regex.Matches(body, "name=\"files\"").Count;
            var bytes = Encoding.UTF8.GetBytes(
                """{"success":true,"visibility":"private","documents":[{"id":1,"filename":"a.docx","status":"uploaded"},{"id":2,"filename":"b.txt","status":"uploaded"}],"skipped":[]}""");
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

            // orgId 지정 → /organizations 안 거침. confirm:true → 미리보기 건너뛰고 바로 업로드.
            var (text, err) = await RunAsync(new { orgId = "org1", path = ".", confirm = true });
            await serverTask;

            Assert.False(err, text);
            Assert.Equal(1, requestCount);   // 한 요청에 전부
            Assert.Equal(2, filesParts);     // files 파트 2개(다중)
            Assert.Contains(L10n.Get("tools.orgDocsUpload.completedHeader", 2, 2), text);
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
