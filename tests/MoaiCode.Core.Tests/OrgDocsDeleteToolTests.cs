using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Knowledge;
using Xunit;

namespace MoaiCode.Core.Tests;

// OrgDocsDelete(문서 삭제) 툴: DELETE {baseUrl}/knowledge/{id}?orgId= 계약 (§6).
// 권한(업로더 본인만)은 서버가 강제 — 클라이언트는 403 을 명확히 표시.
[Collection("EnvMutating")]
public sealed class OrgDocsDeleteToolTests
{
    private static async Task<(string Text, bool Error)> RunAsync(object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in new OrgDocsDeleteTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { sb.Append(o.Text); err |= o.IsError; }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public void Is_a_write_tool() => Assert.False(new OrgDocsDeleteTool().IsReadOnly);

    private async Task<(string Text, bool Error, string? Method, string? Path, string? Query)> WithServer(
        int status, string body, object input)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? method = null, path = null, query = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            method = ctx.Request.HttpMethod;
            path = ctx.Request.Url?.AbsolutePath;
            query = ctx.Request.Url?.Query;
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
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
            var (text, err) = await RunAsync(input);
            await serverTask;
            return (text, err, method, path, query);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
            listener.Stop();
        }
    }

    [Fact]
    public async Task Deletes_with_correct_method_path_and_query()
    {
        var r = await WithServer(200, """{"success":true,"id":123}""",
            new { documentId = "123", orgId = "org1" });

        Assert.False(r.Error, r.Text);
        Assert.Equal("DELETE", r.Method);
        Assert.Equal("/knowledge/123", r.Path);
        Assert.Contains("orgId=org1", r.Query);
        Assert.Contains("삭제됨", r.Text);
    }

    [Fact]
    public async Task Forbidden_reports_owner_only()
    {
        var r = await WithServer(403, """{"error":"forbidden"}""",
            new { documentId = "999", orgId = "org1" });

        Assert.True(r.Error);
        Assert.Contains("본인이 업로드한 문서만", r.Text);
    }

    [Fact]
    public async Task Not_found_reports_missing()
    {
        var r = await WithServer(404, """{"error":"not found"}""",
            new { documentId = "404", orgId = "org1" });

        Assert.True(r.Error);
        Assert.Contains("찾을 수 없습니다", r.Text);
    }

    [Fact]
    public async Task Requires_documentId_and_orgId()
    {
        var (text, err) = await RunAsync(new { documentId = "1" }); // orgId 없음
        Assert.True(err);
        Assert.Contains("orgId", text);
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
