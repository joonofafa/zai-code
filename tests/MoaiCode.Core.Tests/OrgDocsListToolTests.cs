using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Knowledge;
using Xunit;

namespace MoaiCode.Core.Tests;

// OrgDocsList(조직 문서 목록) 툴: GET {baseUrl}/knowledge?orgId= 계약 (moai-code-knowledge-api.md §2).
[Collection("EnvMutating")]
public sealed class OrgDocsListToolTests
{
    private static async Task<(string Text, bool Error)> RunAsync(object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in new OrgDocsListTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { sb.Append(o.Text); err |= o.IsError; }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public async Task Missing_orgId_reports_how_to_get_it()
    {
        var (baseUrl, key) = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
                              Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", "http://127.0.0.1:1");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-x");
            var (text, err) = await RunAsync(new { });
            Assert.True(err);
            Assert.Contains("orgId", text);
            Assert.Contains("org_docs_", text); // orgId 얻는 방법 안내
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
        }
    }

    [Fact]
    public async Task Sends_orgId_and_search_and_renders_items()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? gotPath = null;
        string? gotQuery = null;
        string? gotAuth = null;

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            gotPath = ctx.Request.Url?.AbsolutePath;
            gotQuery = ctx.Request.Url?.Query;
            gotAuth = ctx.Request.Headers["Authorization"];
            var json = """
            {"organizationId":"org1","items":[
              {"id":26,"title":"취업규칙","filename":"rules.pdf","fileType":"application/pdf","fileSize":84213,"visibility":"organization","processingStatus":"completed","ragEnabled":true},
              {"id":27,"title":"내 메모","filename":"memo.txt","fileSize":512,"visibility":"private","processingStatus":"uploaded","ragEnabled":false}
            ],"count":2}
            """;
            var bytes = Encoding.UTF8.GetBytes(json);
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

            var (text, err) = await RunAsync(new { orgId = "org1", search = "규칙" });
            await serverTask;

            Assert.False(err, text);
            Assert.Equal("/knowledge", gotPath);
            Assert.Contains("orgId=org1", gotQuery);
            Assert.Contains("search=", gotQuery);
            Assert.Equal("Bearer sk-test-key", gotAuth);
            Assert.Contains("취업규칙", text);
            Assert.Contains("organization", text);
            Assert.Contains("처리:uploaded", text); // 완료 아닌 상태 표기
            Assert.Contains("RAG off", text);       // ragEnabled=false 표기
            Assert.StartsWith("<system-reminder>", text); // untrusted 경계
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
