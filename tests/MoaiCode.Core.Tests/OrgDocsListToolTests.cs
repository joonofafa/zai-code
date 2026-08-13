using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
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
    public async Task Auto_resolves_orgId_from_login_when_single_org()
    {
        // orgId 생략 → GET /organizations 로 자동 해소(조직 1개) → 그 orgId 로 /knowledge 조회.
        var orgsJson = """{"organizations":[{"id":"solo-org","name":"AI본부","isPrimary":true}],"count":1}""";
        var knowledgeJson = """{"organizationId":"solo-org","items":[{"id":1,"title":"보고서","visibility":"organization"}],"count":1}""";
        var paths = new List<string>();
        var queries = new List<string>();

        await WithRoutingServer(
            route: path => path.Contains("organizations") ? orgsJson : knowledgeJson,
            record: (path, query) => { paths.Add(path); queries.Add(query); },
            requests: 2,
            body: async () =>
            {
                var (text, err) = await RunAsync(new { });   // orgId 없음
                Assert.False(err, text);
                Assert.Contains("보고서", text);
            });

        Assert.Contains("/organizations", paths);
        Assert.Contains("/knowledge", paths);
        Assert.Contains(queries, q => q.Contains("orgId=solo-org")); // 자동 해소된 id 사용
    }

    [Fact]
    public async Task Multiple_orgs_ask_user_to_pick()
    {
        // 조직이 여러 개면 자동 단정하지 않고 목록과 함께 orgId 지정을 요구.
        var orgsJson = """{"organizations":[{"id":"a","name":"본부A"},{"id":"b","name":"본부B"}],"count":2}""";

        await WithRoutingServer(
            route: _ => orgsJson,
            record: (_, _) => { },
            requests: 1,
            body: async () =>
            {
                var (text, err) = await RunAsync(new { });   // orgId 없음
                Assert.True(err);
                Assert.Contains(
                    L10n.Get("tools.orgResolver.ambiguous", "OrgDocsList", "[a] 본부A, [b] 본부B"),
                    text);
                Assert.Contains("[a]", text);
                Assert.Contains("[b]", text);
            });
    }

    // 경로별 응답을 돌려주는 라우팅 HttpListener 로 지정 횟수만큼 요청을 처리한다.
    private static async Task WithRoutingServer(
        Func<string, string> route, Action<string, string> record, int requests, Func<Task> body)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverTask = Task.Run(async () =>
        {
            for (var n = 0; n < requests; n++)
            {
                var ctx = await listener.GetContextAsync();
                var path = ctx.Request.Url?.AbsolutePath ?? "";
                record(path, ctx.Request.Url?.Query ?? "");
                var bytes = Encoding.UTF8.GetBytes(route(path));
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        });

        var (baseUrl, key) = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL"),
                              Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", $"http://127.0.0.1:{port}");
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-key");
            await body();
            await serverTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
            listener.Stop();
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
            Assert.Contains(L10n.Get("tools.orgDocsList.processing", "uploaded"), text); // 완료 아닌 상태 표기
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
