using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Tools.Knowledge;
using Xunit;

namespace MoaiCode.Core.Tests;

// OrgList(소속 조직 목록) 툴: GET {baseUrl}/organizations 계약.
[Collection("EnvMutating")]
public sealed class OrgListToolTests
{
    private static async Task<(string Text, bool Error)> RunAsync()
    {
        var json = JsonSerializer.SerializeToElement(new { });
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in new OrgListTool().ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o) { sb.Append(o.Text); err |= o.IsError; }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public void Is_read_only() => Assert.True(new OrgListTool().IsReadOnly);

    [Fact]
    public async Task Lists_orgs_with_primary_and_path()
    {
        var body = """{"organizations":[{"id":"o1","name":"AI본부","role":"member","isPrimary":true},{"id":"o2","name":"플랫폼팀","role":"manager"}],"count":2}""";
        var r = await WithServer(200, body, recordPath: true);

        Assert.False(r.Error, r.Text);
        Assert.Equal("/organizations", r.Path);
        Assert.Equal("Bearer sk-test-key", r.Auth);
        Assert.Contains("[o1] AI본부", r.Text);
        Assert.Contains(L10n.Get("tools.orgList.primary"), r.Text);         // isPrimary 표기
        Assert.Contains("[o2] 플랫폼팀", r.Text);
        Assert.StartsWith("<system-reminder>", r.Text); // untrusted 경계
    }

    [Fact]
    public async Task Endpoint_missing_reports_deploy_needed()
    {
        var r = await WithServer(404, """{"error":"not found"}""", recordPath: false);
        Assert.True(r.Error);
        Assert.Contains("/api/v1/organizations", r.Text);
    }

    private async Task<(string Text, bool Error, string? Path, string? Auth)> WithServer(
        int status, string body, bool recordPath)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string? path = null, auth = null;
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            path = ctx.Request.Url?.AbsolutePath;
            auth = ctx.Request.Headers["Authorization"];
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
            var (text, err) = await RunAsync();
            await serverTask;
            return (text, err, path, auth);
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
