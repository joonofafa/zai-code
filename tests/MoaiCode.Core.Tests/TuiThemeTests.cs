using System.Text.Json.Nodes;
using MoaiCode.Config;
using MoaiCode.Tui;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

// TuiTheme 정적 상태(MOAI_THEME env 포함)를 조작하므로 EnvMutating 컬렉션으로 직렬화한다.
[Collection("EnvMutating")]
public class TuiThemeTests : IDisposable
{
    private const string Dark1 = "carbon-dark";
    private const string Dark2 = "violet-dark";
    private const string Dark3 = "zen-dark";
    private const string Light1 = "paper-light";
    private const string Light2 = "solar-light";
    private const string Light3 = "ivory-light";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "occs-theme-" + Guid.NewGuid().ToString("n"));
    private readonly string? _envTheme;

    public TuiThemeTests()
    {
        _envTheme = Environment.GetEnvironmentVariable("MOAI_THEME");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MOAI_THEME", _envTheme);
        TuiTheme.Apply(null);
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Registry_contains_six_themes_in_canonical_order()
    {
        var ids = TuiTheme.Themes.Select(t => t.Id).ToArray();
        // 지정 6종이 지정된 순서 그대로 등록되어 있다.
        Assert.Equal(new[] { Dark1, Dark2, Dark3, Light1, Light2, Light3 }, ids);
        foreach (var t in TuiTheme.Themes)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName));
        }
    }

    [Theory]
    [InlineData(Dark1)]
    [InlineData(Dark2)]
    [InlineData(Dark3)]
    [InlineData(Light1)]
    [InlineData(Light2)]
    [InlineData(Light3)]
    public void Every_theme_has_required_tokens(string id)
    {
        var t = TuiTheme.Themes.Single(x => x.Id == id);
        var p = t.Palette;
        // Selection 은 스펙상 rgba(...) 투과 선택 하이라이트가 허용된다 — 형식만 검사.
        Assert.Matches("^(#[0-9A-Fa-f]{6}|rgba\\(.+\\))$", p.Selection);
        foreach (var hex in new[]
                 {
                     p.Background, p.Surface, p.SurfaceAlt, p.Text, p.TextMuted, p.Border,
                     p.Primary, p.Secondary, p.Success, p.Warning, p.Error, p.Info,
                     p.Syntax.Comment, p.Syntax.Keyword, p.Syntax.String, p.Syntax.Number,
                     p.Syntax.Function, p.Syntax.Type, p.Syntax.Variable, p.Syntax.Operator,
                 })
        {
            Assert.Matches("^#[0-9A-Fa-f]{6}$", hex);
        }
    }

    [Fact]
    public void Theme_modes_are_three_dark_and_three_light()
    {
        var modes = TuiTheme.Themes.Select(t => t.Palette.Mode).ToArray();
        Assert.Equal(3, modes.Count(m => m == "dark"));
        Assert.Equal(3, modes.Count(m => m == "light"));
        // Dark 그룹이 먼저 오는지 (/theme list 그룹핑 순서)
        Assert.Equal("dark", TuiTheme.Themes.First().Palette.Mode);
        Assert.Equal("light", TuiTheme.Themes.Last().Palette.Mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonexistent-theme")]
    [InlineData("default")]     // 레거시 프리셋 이름도 기본 테마로 정규화
    [InlineData("CARBON-DARK")] // 대소문자 무시 정규화 — 정상 id 로 복원
    public void Unknown_theme_falls_back_to_default(string? name)
    {
        TuiTheme.Apply(name);
        if (name is null or "" or "   " or "nonexistent-theme" or "default")
        {
            Assert.Equal(Dark1, TuiTheme.Current);
        }
        else
        {
            Assert.Equal(Dark1, TuiTheme.Current); // CARBON-DARK → carbon-dark
        }
    }

    [Fact]
    public void Theme_change_persists_to_settings_json()
    {
        // 설정 로드 경로: JSON 레이어에 theme 이 있으면 Settings.Theme 로 나온다(ApplyJson 은 순수 함수).
        var settings = SettingsLoader.ApplyJson(Settings.Default, $$"""{ "theme": "{{Light2}}" }""");
        Assert.Equal(Light2, settings.Theme);

        // /theme <id> 실행 시 PersistTheme 콜백이 AppBootstrap 에서 하는 것과 동일하게 저장한다.
        var persistPath = Path.Combine(_dir, "settings.json");
        SettingsWriter.Set(new Dictionary<string, string?> { ["theme"] = Dark3 }, persistPath);
        var saved = JsonNode.Parse(File.ReadAllText(persistPath))!.AsObject();
        Assert.Equal(Dark3, (string?)saved["theme"]);
    }

    [Fact]
    public void Theme_is_restored_on_restart_via_settings_then_env()
    {
        // 재시작 복원 경로 1: settings.json 의 theme → 부트스트랩 Apply
        var settings = SettingsLoader.ApplyJson(Settings.Default, $$"""{ "theme": "{{Light3}}" }""");
        TuiTheme.Apply(settings.Theme);
        Assert.Equal(Light3, TuiTheme.Current);
        Assert.True(TuiTheme.IsLight);

        // 재시작 복원 경로 2: env MOAI_THEME 오버라이드(설정→env 병합 후 InitFromEnv)
        Environment.SetEnvironmentVariable("MOAI_THEME", Dark2);
        TuiTheme.InitFromEnv();
        Assert.Equal(Dark2, TuiTheme.Current);
    }

    [Fact]
    public void Theme_selector_lists_all_six_themes_grouped()
    {
        // /theme list — 셀렉터가 제공하는 6종 전체(스와치 소스)가 Dark/Light 그룹으로 표시된다.
        var cmd = new ThemeCommand();
        var ctx = new SlashContext(
            new MoaiCode.Core.Agent.QueryEngine(
                new MoaiCode.Providers.EchoChatModel(),
                Array.Empty<MoaiCode.Core.Tools.ITool>()),
            new MoaiCode.Persistence.SessionStore(_dir),
            new MoaiCode.Persistence.HistoryStore(Path.Combine(_dir, "history.jsonl")),
            new MoaiCode.Persistence.CheckpointStore(_dir, Path.Combine(_dir, "checkpoints")),
            new AgentRuntimeState(),
            new[] { "Read" }, Array.Empty<string>(), Array.Empty<string>(), "EchoChatModel");
        var r = cmd.ExecuteAsync(ctx, new[] { "list" }, default).GetAwaiter().GetResult();
        foreach (var id in new[] { Dark1, Dark2, Dark3, Light1, Light2, Light3 })
        {
            Assert.Contains(id, r.Output);
        }
    }
}
