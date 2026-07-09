using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Knowledge;
using Xunit;

namespace MoaiCode.Core.Tests;

// OrgDocs(조직 문서함 검색) 툴: PLAN_OPENMOAI_DOCS.md §3/§4 계약 준수 검증.
public class OrgDocsToolTests
{
    private static async Task<(string Text, bool Error)> RunAsync(OrgDocsTool tool, object input)
    {
        var json = JsonSerializer.SerializeToElement(input);
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in tool.ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
                err |= o.IsError;
            }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public async Task Missing_login_reports_error()
    {
        var (baseUrl, key) = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
                              Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", null);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);

            var (text, err) = await RunAsync(new OrgDocsTool(), new { query = "결제 취소" });
            Assert.True(err);
            Assert.Contains("login", text); // "moai login 으로 로그인하세요"
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
        }
    }

    [Fact]
    public async Task Missing_query_reports_error()
    {
        var (text, err) = await RunAsync(new OrgDocsTool(), new { });
        Assert.True(err);
        Assert.Contains("query", text);
    }

    [Fact]
    public async Task Searches_and_formats_results_with_untrusted_boundary()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? gotPath = null;
        string? gotAuth = null;
        string? gotBody = null;

        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            gotPath = ctx.Request.Url?.AbsolutePath;
            gotAuth = ctx.Request.Headers["Authorization"];
            using (var sr = new StreamReader(ctx.Request.InputStream))
            {
                gotBody = await sr.ReadToEndAsync();
            }

            var json = """
            {"results":[
              {"title":"결제 취소 처리 가이드","snippet":"취소 요청은 최대 3회","scope":"organization","source":"bccard-docs","documentId":"doc_1","score":0.87,"url":"https://vip.bccard.ai/knowledge/documents/doc_1"},
              {"title":"내 메모","snippet":"재시도 간격 지수백오프","scope":"personal","source":"personal","documentId":"doc_2","score":0.71,"url":null}
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

            var (text, err) = await RunAsync(new OrgDocsTool(),
                new { query = "결제 취소 재시도", scope = "all", topK = 5 });
            await serverTask;

            Assert.False(err);
            // 계약: 올바른 경로 + Bearer 인증 + body에 query/scope/topK
            Assert.Equal("/knowledge/search", gotPath);
            Assert.Equal("Bearer sk-test-key", gotAuth);
            Assert.Contains("\"scope\":\"all\"", gotBody);
            Assert.Contains("\"topK\":5", gotBody);
            // 결과 포맷: 제목·[scope]·snippet·url
            Assert.Contains("결제 취소 처리 가이드", text);
            Assert.Contains("[organization]", text);
            Assert.Contains("[personal]", text);
            Assert.Contains("취소 요청은 최대 3회", text);
            Assert.Contains("https://vip.bccard.ai/knowledge/documents/doc_1", text);
            // 외부 데이터 → untrusted 경계로 감쌈
            Assert.StartsWith("<system-reminder>", text);
            Assert.Contains("UNTRUSTED", text);
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
