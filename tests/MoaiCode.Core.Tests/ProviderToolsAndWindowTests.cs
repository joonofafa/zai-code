using System.Net;
using System.Text;
using System.Text.Json;
using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Providers;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 프로바이더별 웹 검색·툴 노출·컨텍스트 창. 예전엔 컨텍스트 창이 시작할 때 한 번 정해지고 z.ai 표에 없는 모델은 모두
/// 200K 로 잡혀, 32K 로 띄운 vLLM 에서는 넘친 뒤에야 압축했다.
/// </summary>
[Collection("EnvMutating")]
public sealed class ProviderToolsAndWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zai-win-" + Guid.NewGuid().ToString("n"));
    private readonly string? _prevHome = Environment.GetEnvironmentVariable("MOAI_HOME");

    public ProviderToolsAndWindowTests() => Environment.SetEnvironmentVariable("MOAI_HOME", _root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MOAI_HOME", _prevHome);
        ActiveProvider.Reset();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Model_list_context_windows_are_read_from_each_providers_field()
    {
        var windows = new Dictionary<string, int>();
        var ids = ModelDirectory.Parse("""
            {"data":[
              {"id":"x/router","context_length":262144},
              {"id":"qwen","max_model_len":32768},
              {"id":"claude-x","max_input_tokens":1000000},
              {"id":"plain"}
            ]}
            """, windows).ToList();

        Assert.Equal(4, ids.Count);
        Assert.Equal(262_144, windows["x/router"]);
        Assert.Equal(32_768, windows["qwen"]);
        Assert.Equal(1_000_000, windows["claude-x"]);
        Assert.False(windows.ContainsKey("plain"));
    }

    [Fact]
    public void Effective_window_falls_back_per_provider()
    {
        Assert.Equal(32_768, new ProviderProfile(ProviderKind.Vllm, "http://h/v1", null, "m", null).EffectiveContextWindow);
        Assert.Equal(1_000_000, new ProviderProfile(ProviderKind.Zai, "u", "k", "glm-5.3", null).EffectiveContextWindow);
        Assert.Equal(65_536, new ProviderProfile(ProviderKind.Vllm, "u", null, "m", null, 65_536).EffectiveContextWindow);
    }

    [Fact]
    public async Task Choosing_a_model_saves_its_window_as_a_number_and_applies_it_live()
    {
        using var server = new StubServer("""{"data":[{"id":"big","max_model_len":131072},{"id":"small"}]}""");
        var applied = new List<int>();
        var setup = new CliProviderSetup(Path.GetTempPath(), new SwitchableChatModel(new EchoChatModel()), new FileCredentialStore(), applied.Add);
        var candidate = new ProviderProfile(ProviderKind.Vllm, server.BaseUrl, null, null, null);

        var (models, error) = await setup.ListModelsAsync(candidate, default);
        Assert.Null(error);
        setup.Apply(candidate with { Model = "big" }, newKey: null);

        Assert.Equal(131_072, applied[^1]);
        var saved = File.ReadAllText(Path.Combine(_root, "settings.json"));
        Assert.Contains("\"contextWindow\": 131072", saved);   // 문자열이면 로더가 정수로 읽지 못한다
        var settings = SettingsLoader.Load(Path.GetTempPath());
        Assert.Equal(131_072, ProviderResolver.Resolve(settings, new FileCredentialStore()).EffectiveContextWindow);

        // 창 크기를 모르는 모델로 바꾸면 예전 값이 남지 않고 기본값(vLLM 32K)으로 간다.
        setup.SetModel("small");
        Assert.Equal(32_768, applied[^1]);
        Assert.DoesNotContain("contextWindow", File.ReadAllText(Path.Combine(_root, "settings.json")));
        Assert.Equal("small", ActiveProvider.Current.Model);
    }

    [Fact]
    public void Saved_window_belongs_to_its_model_only()
    {
        var s = SettingsLoader.ApplyJson(Settings.Default, """
            {"activeProvider":"vllm","providers":{"vllm":{"baseUrl":"http://h/v1","model":"big","contextWindow":131072}}}
            """);
        Assert.Equal(131_072, ProviderResolver.Resolve(s, new FileCredentialStore()).ContextWindow);
        Assert.Null(ProviderResolver.Resolve(s, new FileCredentialStore(), modelOverride: "other").ContextWindow);
    }

    [Fact]
    public void Engine_context_window_can_change_mid_session()
    {
        var engine = new QueryEngine(new EchoChatModel(), [], contextWindowTokens: 1_000_000);
        engine.SetContextWindow(32_768);
        Assert.InRange(engine.ContextWindowTokens, 32_700, 32_800);
    }

    [Fact]
    public async Task Unavailable_tools_are_not_offered_to_the_model()
    {
        ActiveProvider.Current = new ProviderProfile(ProviderKind.Vllm, "http://h/v1", null, "m", null);
        var model = new ToolCapture();
        var engine = new QueryEngine(model, [new WebSearchTool(), new MoaiCode.Tools.Media.ImageAnalysisTool(), new MoaiCode.Tools.Media.ImageCreateTool()]);

        await foreach (var _ in engine.SubmitAsync("hi"))
        {
        }

        Assert.Empty(model.Offered);

        ActiveProvider.Current = new ProviderProfile(ProviderKind.Zai, "u", "k", "glm-5.3", "glm-5.3-flash");
        await foreach (var _ in engine.SubmitAsync("again"))
        {
        }

        Assert.Equal(["WebSearch", "ImageAnalysis", "ImageCreate"], model.Offered);
    }

    [Theory]
    [InlineData(ProviderKind.OpenRouter, "/chat/completions", """
        {"choices":[{"message":{"content":".NET 10 shipped on Nov 11, 2025.","annotations":[
          {"type":"url_citation","url_citation":{"url":"https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/","title":"Announcing .NET 10","content":"Today we announce .NET 10"}}]}}]}
        """)]
    [InlineData(ProviderKind.OpenAI, "/responses", """
        {"output":[{"type":"web_search_call"},{"type":"message","content":[{"type":"output_text","text":".NET 10 shipped on Nov 11, 2025.",
          "annotations":[{"type":"url_citation","url":"https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/","title":"Announcing .NET 10","start_index":0,"end_index":10}]}]}]}
        """)]
    [InlineData(ProviderKind.Anthropic, "/messages", """
        {"content":[{"type":"server_tool_use","id":"srvtoolu_1","name":"web_search","input":{"query":"x"}},
          {"type":"web_search_tool_result","tool_use_id":"srvtoolu_1","content":[{"type":"web_search_result","url":"https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/","title":"Announcing .NET 10","page_age":"2025-11-11","encrypted_content":"abc"}]},
          {"type":"text","text":".NET 10 shipped on Nov 11, 2025.","citations":[{"type":"web_search_result_location","url":"https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/","title":"Announcing .NET 10","cited_text":"..."}]}]}
        """)]
    public async Task Web_search_uses_each_providers_built_in_search(ProviderKind kind, string path, string reply)
    {
        using var server = new StubServer(reply);
        ActiveProvider.Current = new ProviderProfile(kind, server.BaseUrl, "key", "model-x", null);

        var output = await Run(new WebSearchTool(), """{"query":".NET 10 release date"}""");

        Assert.Contains(".NET 10 shipped on Nov 11, 2025.", output);
        Assert.Contains("https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/", output);
        Assert.Contains("Announcing .NET 10", output);
        Assert.EndsWith(path, server.LastPath);
        if (kind == ProviderKind.Anthropic)
        {
            Assert.Equal("key", server.LastHeaders["x-api-key"]);
            Assert.Contains("web_search_20260209", server.LastBody);
        }
        else
        {
            Assert.Equal("Bearer key", server.LastHeaders["Authorization"]);
        }
    }

    private static async Task<string> Run(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(Path.GetTempPath(), PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    private sealed class ToolCapture : IChatModel
    {
        public List<string> Offered { get; private set; } = [];

        public async IAsyncEnumerable<StreamEvent> StreamAsync(IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Offered = tools.Select(t => t.Name).ToList();
            await Task.Yield();
            yield return new TextDelta("ok");
            yield return new TurnCompleted(new Usage(1, 1), "end_turn");
        }
    }

    /// <summary>같은 JSON 을 돌려주는 로컬 HTTP 서버(마지막 요청 경로·헤더·본문 기록).</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _reply;

        public StubServer(string reply)
        {
            _reply = reply;
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string BaseUrl { get; }
        public string LastPath { get; private set; } = "";
        public string LastBody { get; private set; } = "";
        public Dictionary<string, string> LastHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                LastPath = ctx.Request.Url!.AbsolutePath;
                using (var reader = new StreamReader(ctx.Request.InputStream))
                {
                    LastBody = await reader.ReadToEndAsync();
                }

                foreach (var key in ctx.Request.Headers.AllKeys)
                {
                    LastHeaders[key!] = ctx.Request.Headers[key]!;
                }

                var bytes = Encoding.UTF8.GetBytes(_reply);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }
}
