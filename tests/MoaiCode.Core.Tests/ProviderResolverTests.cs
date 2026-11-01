using System.Text.Json;
using MoaiCode.Config;
using MoaiCode.Core;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Providers;
using MoaiCode.Tools.Media;
using MoaiCode.Tools.Web;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 프로바이더 해석 규칙. 예전엔 런처가 MOAI_MODEL 을 늘 내보내고 환경변수가 설정을 이겨 /model 선택이
/// 재시작하면 사라졌다 — 이제 /provider·/model 저장값이 환경변수보다 우선한다.
/// </summary>
[Collection("EnvMutating")]
public sealed class ProviderResolverTests : IDisposable
{
    private static readonly string[] EnvNames =
    [
        "MOAI_MODEL", "ZAI_MODEL", "ZAI_API_KEY", "ZAI_BASE_URL", "OPENAI_API_KEY", "VLLM_BASE_URL",
        "MOAI_VISION_MODEL", "ZAI_VISION_MODEL",
    ];

    private readonly Dictionary<string, string?> _saved = EnvNames.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    public ProviderResolverTests()
    {
        foreach (var n in EnvNames)
        {
            Environment.SetEnvironmentVariable(n, null);
        }
    }

    public void Dispose()
    {
        foreach (var (n, v) in _saved)
        {
            Environment.SetEnvironmentVariable(n, v);
        }

        ActiveProvider.Reset();
    }

    private sealed class MemoryStore(Dictionary<string, string>? values = null) : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = values ?? new();
        public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<string> Keys() => _values.Keys.ToList();
    }

    private static Settings Load(string json) => SettingsLoader.ApplyJson(Settings.Default, json);

    [Fact]
    public void Saved_zai_model_beats_legacy_model_and_launcher_env()
    {
        Environment.SetEnvironmentVariable("MOAI_MODEL", "glm-5.3");   // 런처가 늘 내보내던 값
        var s = Load("""{ "model": "glm-4.6", "providers": { "zai": { "model": "glm-5.2" } } }""");

        var p = ProviderResolver.Resolve(s, new MemoryStore());

        Assert.Equal(ProviderKind.Zai, p.Kind);
        Assert.Equal("glm-5.2", p.Model);
        Assert.Equal(ZaiEndpoint.DefaultBaseUrl, p.BaseUrl);
    }

    [Fact]
    public void Legacy_top_level_model_still_applies_to_zai()
        => Assert.Equal("glm-4.6", ProviderResolver.Resolve(Load("""{ "model": "glm-4.6" }"""), new MemoryStore()).Model);

    [Fact]
    public void Model_override_wins_for_this_run()
    {
        var s = Load("""{ "providers": { "zai": { "model": "glm-5.2" } } }""");
        Assert.Equal("glm-4.7", ProviderResolver.Resolve(s, new MemoryStore(), modelOverride: "glm-4.7").Model);
    }

    [Fact]
    public void Stored_key_beats_environment_key_and_environment_is_the_fallback()
    {
        Environment.SetEnvironmentVariable("ZAI_API_KEY", "from-env");
        Assert.Equal("stored", ProviderResolver.Resolve(Settings.Default,
            new MemoryStore(new() { ["ZAI_API_KEY"] = "stored" })).ApiKey);
        Assert.Equal("from-env", ProviderResolver.Resolve(Settings.Default, new MemoryStore()).ApiKey);
    }

    [Fact]
    public void Other_providers_ignore_the_zai_model_environment()
    {
        // 옛 런처가 내보내는 glm-5.3 이 OpenAI 로 새면 안 된다.
        Environment.SetEnvironmentVariable("MOAI_MODEL", "glm-5.3");
        var s = Load("""{ "activeProvider": "openai" }""");

        var p = ProviderResolver.Resolve(s, new MemoryStore(new() { ["OPENAI_API_KEY"] = "sk-x" }));

        Assert.Equal(ProviderKind.OpenAI, p.Kind);
        Assert.Equal("https://api.openai.com/v1", p.BaseUrl);
        Assert.Null(p.Model);
        Assert.False(p.IsUsable);   // 모델을 고르기 전에는 요청하지 않는다
    }

    [Fact]
    public void Keys_are_kept_per_provider()
    {
        var store = new MemoryStore(new() { ["ZAI_API_KEY"] = "zai-key" });
        var p = ProviderResolver.Resolve(
            Load("""{ "activeProvider": "openrouter", "providers": { "openrouter": { "model": "x/y" } } }"""), store);

        Assert.Null(p.ApiKey);   // z.ai 키가 OpenRouter 로 실려 나가지 않는다
        Assert.False(p.IsUsable);
    }

    [Fact]
    public void Vllm_needs_a_base_url_but_not_a_key()
    {
        var withUrl = ProviderResolver.Resolve(Load("""
            { "activeProvider": "vllm", "providers": { "vllm": { "baseUrl": "http://10.0.0.5:8000/v1/", "model": "qwen" } } }
            """), new MemoryStore());
        Assert.Equal("http://10.0.0.5:8000/v1", withUrl.BaseUrl);
        Assert.True(withUrl.IsUsable);

        var noUrl = ProviderResolver.Resolve(Load("""
            { "activeProvider": "vllm", "providers": { "vllm": { "model": "qwen" } } }
            """), new MemoryStore());
        Assert.Null(noUrl.BaseUrl);
        Assert.False(noUrl.IsUsable);
    }

    [Fact]
    public void Zai_base_url_outside_the_coding_endpoint_falls_back_to_default()
    {
        var s = Load("""{ "providers": { "zai": { "baseUrl": "https://evil.example/api/coding/paas/v4" } } }""");
        Assert.Equal(ZaiEndpoint.DefaultBaseUrl, ProviderResolver.Resolve(s, new MemoryStore()).BaseUrl);
    }

    [Fact]
    public void Empty_vision_model_means_off_and_zai_has_a_default()
    {
        Assert.Equal(ZaiEndpoint.DefaultVisionModel, ProviderResolver.Resolve(Settings.Default, new MemoryStore()).VisionModel);
        Assert.Null(ProviderResolver.Resolve(
            Load("""{ "providers": { "zai": { "visionModel": "" } } }"""), new MemoryStore()).VisionModel);
    }

    [Fact]
    public void Legacy_provider_and_base_url_keys_are_ignored()
    {
        // ~/.zaicode/settings.json 에 남은 upstream 시절 값(vip 서버)이 프로바이더를 바꾸면 안 된다.
        var p = ProviderResolver.Resolve(
            Load("""{ "provider": "openai", "baseUrl": "https://vip.bccard.ai/api/v1" }"""), new MemoryStore());
        Assert.Equal(ProviderKind.Zai, p.Kind);
        Assert.Equal(ZaiEndpoint.DefaultBaseUrl, p.BaseUrl);
    }

    [Fact]
    public void Project_layer_cannot_change_provider_or_address()
    {
        // 신뢰할 수 없는 저장소의 .claude/settings.json 이 키를 다른 주소로 보내게 두면 안 된다.
        var s = SettingsLoader.ApplyJson(Settings.Default, """
            { "activeProvider": "vllm", "providers": { "vllm": { "baseUrl": "http://attacker:8000/v1", "model": "m" } } }
            """, allowSensitive: false, allowAutomation: false, allowPermissionRules: false);
        Assert.Null(s.ActiveProvider);
        Assert.Empty(s.Providers);
    }

    [Fact]
    public void SetProvider_merges_fields_and_activates()
    {
        var path = Path.Combine(Path.GetTempPath(), "zai-settings-" + Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, """{ "language": "ko", "providers": { "zai": { "model": "glm-5.3" } } }""");
        try
        {
            SettingsWriter.SetProvider("vllm", new Dictionary<string, string?>
            {
                ["baseUrl"] = "http://h:8000/v1",
                ["model"] = "qwen",
            }, activate: true, path: path);
            SettingsWriter.SetProvider("vllm", new Dictionary<string, string?> { ["visionModel"] = "" }, path: path);

            var s = SettingsLoader.ApplyJson(Settings.Default, File.ReadAllText(path));
            Assert.Equal("ko", s.Language);
            Assert.Equal("vllm", s.ActiveProvider);
            Assert.Equal("glm-5.3", s.Providers["zai"].Model);
            Assert.Equal(new ProviderSettings("http://h:8000/v1", "qwen", ""), s.Providers["vllm"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Factory_falls_back_to_echo_with_the_reason()
    {
        var model = ProviderFactory.Create(
            new ProviderProfile(ProviderKind.OpenAI, "https://api.openai.com/v1", null, "gpt-x", null), out var desc);
        Assert.IsType<EchoChatModel>(model);
        Assert.Contains("OPENAI_API_KEY", desc);

        var ok = ProviderFactory.Create(
            new ProviderProfile(ProviderKind.Vllm, "http://h:8000/v1", null, "qwen", null), out var okDesc);
        Assert.IsType<RetryingChatModel>(ok);
        Assert.Equal("vLLM · qwen @ http://h:8000/v1", okDesc);
    }

    [Fact]
    public async Task Zai_only_tools_refuse_other_providers()
    {
        ActiveProvider.Current = new ProviderProfile(ProviderKind.OpenAI, "https://api.openai.com/v1", "sk-x", "gpt-x", null);
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);

        var search = await Run(new WebSearchTool(), """{"query":"x"}""", ctx);
        Assert.Equal(L10n.Get("tools.webSearch.unsupportedProvider", "OpenAI"), search);

        var vision = await Run(new ImageAnalysisTool(), """{"image_source":"a.png","prompt":"x"}""", ctx);
        Assert.Equal(L10n.Get("tools.imageAnalysis.noModel"), vision);
    }

    private static async Task<string> Run(ITool tool, string json, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(json);
        var text = "";
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, ctx, default))
        {
            if (p is ToolOutput o)
            {
                text += o.Text;
            }
        }

        return text;
    }
}
