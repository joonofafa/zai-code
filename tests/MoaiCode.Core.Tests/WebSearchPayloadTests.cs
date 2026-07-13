using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>공유 전역 env(OPENAI_BASE_URL/KEY)를 건드리는 테스트를 직렬화하는 collection.</summary>
[CollectionDefinition("EnvMutating", DisableParallelization = true)]
public sealed class EnvMutatingCollection
{
}

/// <summary>
/// WebSearch 요청 페이로드 계약. 예전엔 language/country 를 ko/KR 로 강제해 영어 기술 질의가
/// 한국 소스로 쏠렸다 → 이제 사용자가 명시했을 때만 보낸다.
/// </summary>
[Collection("EnvMutating")]
public sealed class WebSearchPayloadTests
{
    private static async Task<string> CaptureBodyAsync(object input)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        string body = "";
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            using (var sr = new StreamReader(ctx.Request.InputStream))
            {
                body = await sr.ReadToEndAsync();
            }

            var bytes = Encoding.UTF8.GetBytes("""{"results":[],"count":0}""");
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

            var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
            var json = JsonSerializer.SerializeToElement(input);
            await foreach (var _ in new WebSearchTool().ExecuteAsync(json, ctx, CancellationToken.None))
            {
            }

            await serverTask;
            return body;
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_BASE_URL", baseUrl);
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", key);
            listener.Stop();
        }
    }

    [Fact]
    public async Task Does_not_force_locale_when_unspecified()
    {
        var body = await CaptureBodyAsync(new { query = "python asyncio" });

        Assert.Contains("\"query\":\"python asyncio\"", body);
        Assert.DoesNotContain("language", body); // ko 강제 안 함
        Assert.DoesNotContain("country", body);  // KR 강제 안 함
    }

    [Fact]
    public async Task Sends_locale_only_when_user_provides_it()
    {
        var body = await CaptureBodyAsync(new { query = "뉴스", language = "ko", country = "KR" });

        Assert.Contains("\"language\":\"ko\"", body);
        Assert.Contains("\"country\":\"KR\"", body);
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
