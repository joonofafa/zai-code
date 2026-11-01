using System.Net;
using System.Text;
using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Core;
using MoaiCode.Localization;
using MoaiCode.Providers;
using MoaiCode.Tui;
using MoaiCode.Tui.Commands;
using MoaiCode.Tui.Input;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>/provider 흐름·키 마스킹·모델 목록 조회. 접속 확인에 실패하거나 중간에 취소하면 아무것도 저장하지 않아야 한다.</summary>
public sealed class ProviderCommandTests
{
    private const string Key = "sk-or-v1-0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("", "")]
    [InlineData("short", "*****")]
    [InlineData("12345678", "********")]
    [InlineData("123456789", "12345678*")]
    public void Mask_reveals_only_the_first_eight_characters(string secret, string expected)
        => Assert.Equal(expected, SecretMask.Mask(secret));

    [Fact]
    public void Mask_caps_the_stars_for_long_keys()
    {
        var masked = SecretMask.Mask(Key);
        Assert.StartsWith("sk-or-v1" + new string('*', 24), masked);
        Assert.EndsWith($" ({Key.Length})", masked);
    }

    [Fact]
    public void Pasted_key_is_never_echoed_past_the_first_eight_characters()
    {
        var events = new Queue<InputEvent?>(
        [
            new PasteEvent(Key + "\r\n"),
            new KeyEvent(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)),
        ]);
        var screen = new StringBuilder();

        var read = SecretPrompt.Read("key: ", events.Dequeue, s => screen.Append(s));

        Assert.Equal(Key, read);   // 붙여넣기에 딸려 온 줄바꿈은 버린다
        Assert.DoesNotContain(Key[8..16], screen.ToString());
    }

    [Fact]
    public void Escape_cancels_and_backspace_edits()
    {
        var typed = new Queue<InputEvent?>(
        [
            new KeyEvent(new ConsoleKeyInfo('a', ConsoleKey.A, false, false, false)),
            new KeyEvent(new ConsoleKeyInfo('b', ConsoleKey.B, false, false, false)),
            new KeyEvent(new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false)),
            new KeyEvent(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false)),
        ]);
        Assert.Equal("a", SecretPrompt.Read("k: ", typed.Dequeue, _ => { }));

        var esc = new Queue<InputEvent?>([new KeyEvent(new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, false, false, false))]);
        Assert.Null(SecretPrompt.Read("k: ", esc.Dequeue, _ => { }));
    }

    // ---- /provider 흐름 ----

    private sealed class FakeSetup : IProviderSetup
    {
        public ProviderProfile Current { get; set; } =
            new(ProviderKind.Zai, ZaiEndpoint.DefaultBaseUrl, "zai-key-123456789", "glm-5.3", "glm-5.3-flash");

        public Dictionary<ProviderKind, ProviderProfile> Effectives { get; } = new();
        public (IReadOnlyList<string> Models, string? Error) Listing { get; set; } = (["a-model", "b-model"], null);
        public ProviderProfile? Listed { get; private set; }
        public (ProviderProfile Profile, string? NewKey)? Applied { get; private set; }

        public ProviderProfile Effective(ProviderKind kind)
            => Effectives.TryGetValue(kind, out var p) ? p
                : new ProviderProfile(kind, ProviderCatalog.DefaultBaseUrl(kind), null, null, null);

        public bool HasStoredKey(ProviderKind kind) => kind == ProviderKind.Zai;

        public Task<(IReadOnlyList<string> Models, string? Error)> ListModelsAsync(ProviderProfile candidate, CancellationToken ct)
        {
            Listed = candidate;
            return Task.FromResult(Listing);
        }

        public string Apply(ProviderProfile profile, string? newKey)
        {
            Applied = (profile, newKey);
            return "applied";
        }

        public void SetVisionModel(string visionModel) => throw new NotSupportedException();
    }

    // 각본: Select 는 제목 순서대로 고를 위치, ReadSecret·ReadLine 은 입력값.
    private static PromptIo Script(Queue<int> picks, Queue<string?> secrets, Queue<string?> lines, List<string>? titles = null)
        => new(
            (title, items, _) => { titles?.Add(title + "|" + string.Join(",", items)); return picks.Dequeue(); },
            _ => secrets.Dequeue(),
            _ => lines.Dequeue(),
            _ => { });

    private static int Index(ProviderKind kind) => ProviderCatalog.All.ToList().IndexOf(kind);

    [Fact]
    public async Task OpenRouter_with_a_pasted_key_is_applied_after_a_successful_connection()
    {
        var setup = new FakeSetup();
        var many = Enumerable.Range(0, 40).Select(i => $"vendor/model-{i:00}").Append("qwen/qwen3-coder").ToList();
        setup.Listing = (many, null);
        var io = Script(
            new Queue<int>([Index(ProviderKind.OpenRouter), 0, 0]),   // 프로바이더 → 모델(거른 뒤 첫 항목) → 이미지 분석 "사용 안 함"
            new Queue<string?>([Key]),
            new Queue<string?>(["qwen", ""]));                       // 모델 거르기 "qwen", 비전 거르기는 전체

        var result = await ProviderFlow.RunAsync(io, setup, default);

        Assert.Equal(L10n.Get("slash.provider.switched", "applied"), result);
        Assert.Equal(Key, setup.Listed!.ApiKey);
        var (profile, newKey) = setup.Applied!.Value;
        Assert.Equal(ProviderKind.OpenRouter, profile.Kind);
        Assert.Equal("qwen/qwen3-coder", profile.Model);
        Assert.Null(profile.VisionModel);
        Assert.Equal(Key, newKey);
    }

    [Fact]
    public async Task Failed_connection_changes_nothing()
    {
        var setup = new FakeSetup { Listing = ([], "HTTP 401") };
        var io = Script(new Queue<int>([Index(ProviderKind.OpenAI)]), new Queue<string?>(["sk-bad-key-000000"]), new Queue<string?>());

        var result = await ProviderFlow.RunAsync(io, setup, default);

        Assert.Equal(L10n.Get("slash.provider.connectFailed", "OpenAI", "HTTP 401"), result);
        Assert.Null(setup.Applied);
    }

    [Fact]
    public async Task Enter_keeps_the_existing_key_and_current_vision_model_stays_selectable()
    {
        var setup = new FakeSetup();
        setup.Effectives[ProviderKind.Zai] = setup.Current;
        var titles = new List<string>();
        var io = Script(
            new Queue<int>([Index(ProviderKind.Zai), 0, 1]),   // 비전 목록 1번 = 목록에 없던 현재 비전 모델
            new Queue<string?>([""]),
            new Queue<string?>(),
            titles);

        await ProviderFlow.RunAsync(io, setup, default);

        var (profile, newKey) = setup.Applied!.Value;
        Assert.Null(newKey);                                   // 키는 다시 저장하지 않는다
        Assert.Equal("zai-key-123456789", profile.ApiKey);
        Assert.Equal("glm-5.3-flash", profile.VisionModel);
        Assert.Contains(titles, t => t.Contains("glm-5.3-flash"));
    }

    [Fact]
    public async Task Missing_key_or_cancel_changes_nothing()
    {
        var setup = new FakeSetup();
        Assert.Equal(L10n.Get("slash.provider.keyRequired", "Claude"), await ProviderFlow.RunAsync(
            Script(new Queue<int>([Index(ProviderKind.Anthropic)]), new Queue<string?>([""]), new Queue<string?>()), setup, default));
        Assert.Equal(L10n.Get("common.unchanged"), await ProviderFlow.RunAsync(
            Script(new Queue<int>([-1]), new Queue<string?>(), new Queue<string?>()), setup, default));
        Assert.Equal(L10n.Get("common.unchanged"), await ProviderFlow.RunAsync(
            Script(new Queue<int>([Index(ProviderKind.OpenAI)]), new Queue<string?>([null]), new Queue<string?>()), setup, default));
        Assert.Null(setup.Applied);
    }

    [Fact]
    public async Task Vllm_asks_for_a_url_and_works_without_a_key()
    {
        var setup = new FakeSetup();
        Assert.Equal(L10n.Get("slash.provider.badUrl", "ftp://x"), await ProviderFlow.RunAsync(
            Script(new Queue<int>([Index(ProviderKind.Vllm)]), new Queue<string?>([""]), new Queue<string?>(["ftp://x"])), setup, default));
        Assert.Null(setup.Applied);

        await ProviderFlow.RunAsync(
            Script(new Queue<int>([Index(ProviderKind.Vllm), 1, 2]), new Queue<string?>([""]), new Queue<string?>(["http://10.0.0.5:8000/v1/"])),
            setup, default);
        var (profile, _) = setup.Applied!.Value;
        Assert.Equal("http://10.0.0.5:8000/v1", profile.BaseUrl);
        Assert.Null(profile.ApiKey);
        Assert.Equal("b-model", profile.Model);
        Assert.Equal("b-model", profile.VisionModel);
    }

    // ---- 모델 목록 조회 ----

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public async Task Listing_reports_the_server_refusal_instead_of_an_empty_list()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"Incorrect API key"}}""");
        var r = await ModelDirectory.ListAsync(new HttpClient(handler), ProviderKind.OpenAI, "https://api.openai.com/v1", "sk-x", default);
        Assert.Empty(r.Models);
        Assert.Contains("401", r.Error);
        Assert.Contains("Incorrect API key", r.Error);
    }

    [Fact]
    public async Task OpenAI_listing_drops_non_chat_models_and_vllm_sends_no_empty_bearer()
    {
        var openai = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"gpt-x"},{"id":"text-embedding-3-small"},{"id":"whisper-1"}]}""");
        var r = await ModelDirectory.ListAsync(new HttpClient(openai), ProviderKind.OpenAI, "https://api.openai.com/v1", "sk-x", default);
        Assert.Equal(["gpt-x"], r.Models);

        var vllm = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"qwen"}]}""");
        await ModelDirectory.ListAsync(new HttpClient(vllm), ProviderKind.Vllm, "http://h:8000/v1", null, default);
        Assert.Null(vllm.Last!.Headers.Authorization);
        Assert.Equal("http://h:8000/v1/models", vllm.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Claude_listing_uses_its_own_headers()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"data":[{"id":"claude-x","type":"model"}],"has_more":false}""");
        var r = await ModelDirectory.ListAsync(new HttpClient(handler), ProviderKind.Anthropic, "https://api.anthropic.com/v1", "sk-ant-x", default);
        Assert.Equal(["claude-x"], r.Models);
        Assert.Equal("sk-ant-x", handler.Last!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2026-10-21", handler.Last.Headers.GetValues("anthropic-version").Single());
        Assert.Null(handler.Last.Headers.Authorization);
    }
}

/// <summary>Cli 쪽 저장·라이브 교체 — 실제 settings.json·credentials.json 을 MOAI_HOME 격리 루트에 쓴다.</summary>
[Collection("EnvMutating")]
public sealed class CliProviderSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zai-prov-" + Guid.NewGuid().ToString("n"));
    private readonly string? _prevHome = Environment.GetEnvironmentVariable("MOAI_HOME");

    public CliProviderSetupTests() => Environment.SetEnvironmentVariable("MOAI_HOME", _root);

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
    public void Apply_saves_key_and_choice_then_swaps_the_live_model()
    {
        var model = new SwitchableChatModel(new EchoChatModel());
        var creds = new FileCredentialStore();
        var setup = new CliProviderSetup(Path.GetTempPath(), model, creds);

        var desc = setup.Apply(new ProviderProfile(ProviderKind.Vllm, "http://h:8000/v1", null, "qwen", null), newKey: null);

        Assert.Equal("vLLM · qwen @ http://h:8000/v1", desc);
        Assert.IsType<RetryingChatModel>(model.Inner);
        Assert.Equal(ProviderKind.Vllm, ActiveProvider.Current.Kind);
        var saved = SettingsLoader.Load(Path.GetTempPath());
        Assert.Equal("vllm", saved.ActiveProvider);
        Assert.Equal(new ProviderSettings("http://h:8000/v1", "qwen", ""), saved.Providers["vllm"]);

        // 다음 실행에서도 같은 프로바이더로 시작한다(재시작해도 선택이 남는다).
        Assert.Equal(ActiveProvider.Current, ProviderResolver.Resolve(saved, creds));

        setup.Apply(new ProviderProfile(ProviderKind.OpenRouter, "https://openrouter.ai/api/v1", "sk-or-v1-abc", "x/y", "x/v"), "sk-or-v1-abc");
        Assert.Equal("sk-or-v1-abc", creds.Get("OPENROUTER_API_KEY"));
        Assert.True(setup.HasStoredKey(ProviderKind.OpenRouter));
        Assert.Equal("qwen", setup.Effective(ProviderKind.Vllm).Model);   // 다른 프로바이더의 선택은 그대로 남는다

        setup.SetVisionModel("");
        Assert.Null(ActiveProvider.Current.VisionModel);
        Assert.Equal("", SettingsLoader.Load(Path.GetTempPath()).Providers["openrouter"].VisionModel);
    }
}
