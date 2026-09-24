using Spectre.Console;

namespace MoaiCode.Tui;

/// <summary>한 테마의 색 토큰 모음(raw HEX — 컴포넌트가 직접 쓰지 않는다, TuiTheme 경유로만).</summary>
public sealed record ThemePalette(
    string Mode,
    string Background,
    string Surface,
    string SurfaceAlt,
    string Text,
    string TextMuted,
    string Border,
    string Primary,
    string Secondary,
    string Success,
    string Warning,
    string Error,
    string Info,
    string Selection,
    string DiffAddedBg,
    string DiffRemovedBg,
    ThemeSyntax Syntax);

public sealed record ThemeSyntax(
    string Comment,
    string Keyword,
    string String,
    string Number,
    string Function,
    string Type,
    string Variable,
    string Operator);

/// <summary>등록된 테마 정의(제품 표시명 + 팔레트). UI 는 표시명만 쓰고 원 팔레트 출처는 THIRD_PARTY_NOTICES.md 에 표기한다.</summary>
public sealed record ThemeInfo(string Id, string DisplayName, ThemePalette Palette);

/// <summary>
/// TUI 색 테마의 단일 지점. 6개 시맨틱 테마(다크 3·라이트 3)를 등록한다.
/// 컴포넌트는 raw HEX/ANSI 색 이름을 직접 쓰지 않고 <see cref="Mark"/>(Spectre 마크업용),
/// <see cref="Fg"/>/<see cref="Bg"/>(raw ANSI 위젯용), <see cref="ColorOf"/>(Spectre Color)로
/// 시맨틱 역할(<see cref="Role"/>)을 요청한다.
/// 결정 순서: /theme(런타임) → 설정 theme → env MOAI_THEME → 기본 carbon-dark. Cli 가 부트스트랩에서 <see cref="Apply"/>.
/// 레거시 프리셋 이름(default/bright/high-contrast)이 들어오면 기본 테마로 정규화한다.
/// </summary>
public static class TuiTheme
{
    public static string Default => "carbon-dark";

    public static readonly IReadOnlyList<ThemeInfo> Themes = new[]
    {
        new ThemeInfo("carbon-dark", "Carbon Dark", CarbonDark()),
        new ThemeInfo("violet-dark", "Violet Dark", VioletDark()),
        new ThemeInfo("zen-dark", "Zen Dark", ZenDark()),
        new ThemeInfo("paper-light", "Paper Light", PaperLight()),
        new ThemeInfo("solar-light", "Solar Light", SolarLight()),
        new ThemeInfo("ivory-light", "Ivory Light", IvoryLight()),
    };

    private static ThemeInfo _current = Themes[0];

    /// <summary>현재 테마 정의(팔레트 포함). /theme 표시·테스트용.</summary>
    public static ThemeInfo CurrentTheme => _current;

    /// <summary>현재 테마 id.</summary>
    public static string Current => _current.Id;

    /// <summary>현재 테마가 라이트 계열이면 true.</summary>
    public static bool IsLight => _current.Palette.Mode == "light";

    /// <summary>보조 텍스트 마크업 색(기존 호출부 호환 — Mark(Role.Dim) 과 동일).</summary>
    public static string Dim => Mark(Role.Dim);

    /// <summary>시맨틱 색 역할. 컴포넌트는 이 이름으로 색을 요청한다(예: TuiTheme.Mark(TuiTheme.Role.Success)).</summary>
    public enum Role
    {
        Dim,        // 보조/희미한 텍스트(툴 호출·결과 미리보기·메타)
        Accent,     // 브랜드 강조(패널 헤더·타이틀)
        Prompt,     // 입력 프롬프트 화살표 '❯'
        Success,    // 성공/완료 ✓
        Warning,    // 경고/주의 상태
        Error,      // 오류 ✗
        Info,       // 정보성 강조(질문·선택 확정·모드 표기)
        Border,     // 패널 테두리·구분선
        Text,       // 본문보다 밝은 강조 텍스트(숫자·키 이름)
        Muted,      // 입력 힌트·echo된 셸 명령(Dim 보다 한 단계 어두움)
        ShellDim,   // 셸 버블 테두리
        ShellBody,  // 셸 버블 본문
        UserBorder, // 일반 사용자 버블 테두리
        UserBody,   // 일반 사용자 버블 본문
        Ghost,      // 자동완성 ghost 텍스트
        Selected,   // 선택된 목록 항목
        Help,       // 위젯 도움말 줄
        DiffAdded,
        DiffRemoved,
        InputBg,    // 입력 라인 배경(라이트 테마 폴백용 — Surface 토큰)
    }

    /// <summary>테마 적용. 알 수 없는/레거시 이름은 기본 테마로 폴백. env 초기화·/theme·부트스트랩이 호출.</summary>
    public static void Apply(string? name)
        => _current = Themes.FirstOrDefault(t => t.Id == Normalize(name)) ?? Themes[0];

    /// <summary>입력을 알려진 테마 id 로 정규화. 6테마 밖의 값(레거시 프리셋 포함)은 기본 테마.</summary>
    public static string Normalize(string? name)
    {
        var v = name?.Trim().ToLowerInvariant().Replace("_", "-");
        return Themes.Any(t => t.Id == v) ? v! : Default;
    }

    /// <summary>env MOAI_THEME 로 초기화(설정→env 는 Cli 가 병합해 내보낸다). 미설정이면 기본 테마.</summary>
    public static void InitFromEnv()
        => Apply(Environment.GetEnvironmentVariable("MOAI_THEME"));

    // ── 컴포넌트 진입 API ────────────────────────────────────────────────

    /// <summary>Spectre 마크업용 색 문자열(예: "#abb2bf") — $"[{TuiTheme.Mark(role)}]…[/]".</summary>
    public static string Mark(Role role) => "#" + HexOf(role);

    /// <summary>raw ANSI foreground 시퀀스. truecolor 우선, 미지원 터미널은 xterm 256색으로 근사.</summary>
    public static string Fg(Role role) => FgSequence(HexOf(role));

    /// <summary>raw ANSI background 시퀀스(입력 라인 배경 등).</summary>
    public static string Bg(Role role) => BgSequence(HexOf(role));

    /// <summary>Spectre Color 값(Panel.BorderColor 등).</summary>
    public static Color ColorOf(Role role) => ParseHexColor(HexOf(role));

    // ── 역할 → 팔레트 매핑 ────────────────────────────────────────────────

    private static string HexOf(Role role) => role switch
    {
        Role.Dim => _current.Palette.TextMuted,
        Role.Accent => _current.Palette.Primary,
        Role.Prompt => _current.Palette.Success,
        Role.Success => _current.Palette.Success,
        Role.Warning => _current.Palette.Warning,
        Role.Error => _current.Palette.Error,
        Role.Info => _current.Palette.Info,
        Role.Border => _current.Palette.Border,
        Role.Text => _current.Palette.Text,
        Role.Muted => _current.Palette.TextMuted,
        Role.ShellDim => _current.Palette.Border,
        Role.ShellBody => _current.Palette.TextMuted,
        Role.UserBorder => _current.Palette.Primary,
        Role.UserBody => _current.Palette.Text,
        Role.Ghost => _current.Palette.TextMuted,
        Role.Selected => _current.Palette.Primary,
        Role.Help => _current.Palette.TextMuted,
        Role.DiffAdded => _current.Palette.Success,
        Role.DiffRemoved => _current.Palette.Error,
        Role.InputBg => _current.Palette.Surface,
        _ => _current.Palette.Text,
    };

    // ── ANSI 변환 ────────────────────────────────────────────────────────

    // truecolor 지원 판정: COLORTERM(truecolor/24bit) 또는 WT_SESSION(Windows Terminal).
    // 미지원이면 헥스를 xterm 256 팔레트로 근사 — 레거시 conhost 다운샘플 대응(기존 256색 설계 계승).
    private static readonly bool TrueColor = IsTrueColor();

    private static bool IsTrueColor()
    {
        var ct = Environment.GetEnvironmentVariable("COLORTERM");
        return (ct is not null && ct.Contains("truecolor", StringComparison.OrdinalIgnoreCase))
            || (ct is not null && ct.Contains("24bit", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("WT_SESSION") is not null;
    }

    private static string FgSequence(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return TrueColor ? $"\x1b[38;2;{r};{g};{b}m" : $"\x1b[38;5;{To256(r, g, b)}m";
    }

    private static string BgSequence(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return TrueColor ? $"\x1b[48;2;{r};{g};{b}m" : $"\x1b[48;5;{To256(r, g, b)}m";
    }

    private static (int R, int G, int B) ParseHex(string hex)
    {
        var h = hex.StartsWith('#') ? hex[1..] : hex;
        if (h.Length == 6
            && int.TryParse(h[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            && int.TryParse(h[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            && int.TryParse(h[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return (r, g, b);
        }

        return (0x80, 0x80, 0x80); // 파싱 불가 시 중립 회색(렌더 유지)
    }

    private static Color ParseHexColor(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        return new Color((byte)r, (byte)g, (byte)b);
    }

    // xterm 256 근사: 6x6x6 큐브(16..231) + 회색 램프(232..255) 중 근사 오차 작은 쪽.
    private static readonly int[] Cube = { 0, 95, 135, 175, 215, 255 };

    private static int To256(int r, int g, int b)
    {
        int Near(int v)
        {
            var best = 0;
            for (var i = 1; i < Cube.Length; i++)
            {
                if (Math.Abs(Cube[i] - v) < Math.Abs(Cube[best] - v))
                {
                    best = i;
                }
            }

            return best;
        }

        var (nr, ng, nb) = (Near(r), Near(g), Near(b));
        var cubeDist = Math.Abs(Cube[nr] - r) + Math.Abs(Cube[ng] - g) + Math.Abs(Cube[nb] - b);

        var grayIdx = Math.Clamp((int)Math.Round(((r + g + b) / 3.0 - 8) / 10.0), 0, 23);
        var grayVal = 8 + grayIdx * 10;
        var grayDist = Math.Abs(grayVal - r) + Math.Abs(grayVal - g) + Math.Abs(grayVal - b);

        return cubeDist <= grayDist ? 16 + nr * 36 + ng * 6 + nb : 232 + grayIdx;
    }

    // ── 테마 팔레트(시맨틱 값 — 원 팔레트 출처는 THIRD_PARTY_NOTICES.md 에 표기) ──

    private static ThemePalette CarbonDark() => new(
        Mode: "dark",
        Background: "#282C34", Surface: "#21252B", SurfaceAlt: "#2C313A",
        Text: "#ABB2BF", TextMuted: "#7D8799", Border: "#3E4451",
        Primary: "#61AFEF", Secondary: "#C678DD",
        Success: "#98C379", Warning: "#E5C07B", Error: "#E06C75", Info: "#56B6C2",
        Selection: "#3E4451",
        DiffAddedBg: "rgba(152, 195, 121, 0.14)", DiffRemovedBg: "rgba(224, 108, 117, 0.14)",
        Syntax: new ThemeSyntax(
            Comment: "#7D8799", Keyword: "#C678DD", String: "#98C379", Number: "#D19A66",
            Function: "#61AFEF", Type: "#E5C07B", Variable: "#E06C75", Operator: "#56B6C2"));

    private static ThemePalette VioletDark() => new(
        Mode: "dark",
        Background: "#282A36", Surface: "#21222C", SurfaceAlt: "#44475A",
        Text: "#F8F8F2", TextMuted: "#6272A4", Border: "#44475A",
        Primary: "#BD93F9", Secondary: "#FF79C6",
        Success: "#50FA7B", Warning: "#F1FA8C", Error: "#FF5555", Info: "#8BE9FD",
        Selection: "#44475A",
        DiffAddedBg: "rgba(80, 250, 123, 0.12)", DiffRemovedBg: "rgba(255, 85, 85, 0.12)",
        Syntax: new ThemeSyntax(
            Comment: "#6272A4", Keyword: "#FF79C6", String: "#F1FA8C", Number: "#BD93F9",
            Function: "#50FA7B", Type: "#8BE9FD", Variable: "#F8F8F2", Operator: "#FF79C6"));

    private static ThemePalette ZenDark() => new(
        Mode: "dark",
        Background: "#393939", Surface: "#333333", SurfaceAlt: "#454545",
        Text: "#DCDCCC", TextMuted: "#808080", Border: "#505050",
        Primary: "#8CD0D3", Secondary: "#DC8CC3",
        Success: "#7F9F7F", Warning: "#F0DFAF", Error: "#CC9393", Info: "#8FBEDE",
        Selection: "#4F4F4F",
        DiffAddedBg: "rgba(127, 159, 127, 0.16)", DiffRemovedBg: "rgba(204, 147, 147, 0.16)",
        Syntax: new ThemeSyntax(
            Comment: "#7F9F7F", Keyword: "#F0DFAF", String: "#CC9393", Number: "#8CD0D3",
            Function: "#EFEF8F", Type: "#9AC39F", Variable: "#DCDCCC", Operator: "#F0EFD0"));

    private static ThemePalette PaperLight() => new(
        Mode: "light",
        Background: "#FFFFFF", Surface: "#F6F8FA", SurfaceAlt: "#EAEEF2",
        Text: "#24292F", TextMuted: "#57606A", Border: "#D0D7DE",
        Primary: "#0969DA", Secondary: "#8250DF",
        Success: "#1A7F37", Warning: "#9A6700", Error: "#CF222E", Info: "#0969DA",
        Selection: "rgba(9, 105, 218, 0.14)",
        DiffAddedBg: "rgba(26, 127, 55, 0.12)", DiffRemovedBg: "rgba(207, 34, 46, 0.11)",
        Syntax: new ThemeSyntax(
            Comment: "#6E7781", Keyword: "#CF222E", String: "#0A3069", Number: "#0550AE",
            Function: "#8250DF", Type: "#953800", Variable: "#24292F", Operator: "#CF222E"));

    private static ThemePalette SolarLight() => new(
        Mode: "light",
        Background: "#FDF6E3", Surface: "#EEE8D5", SurfaceAlt: "#F5EFD9",
        Text: "#657B83", TextMuted: "#93A1A1", Border: "#D9D2BF",
        Primary: "#268BD2", Secondary: "#6C71C4",
        Success: "#859900", Warning: "#B58900", Error: "#DC322F", Info: "#2AA198",
        Selection: "rgba(38, 139, 210, 0.16)",
        DiffAddedBg: "rgba(133, 153, 0, 0.13)", DiffRemovedBg: "rgba(220, 50, 47, 0.12)",
        Syntax: new ThemeSyntax(
            Comment: "#93A1A1", Keyword: "#859900", String: "#2AA198", Number: "#6C71C4",
            Function: "#268BD2", Type: "#B58900", Variable: "#657B83", Operator: "#859900"));

    private static ThemePalette IvoryLight() => new(
        Mode: "light",
        Background: "#FCFCFC", Surface: "#F8F9FA", SurfaceAlt: "#FAFAFA",
        Text: "#5C6166", TextMuted: "#828E9F", Border: "#DDE1E5",
        Primary: "#F29718", Secondary: "#A37ACC",
        Success: "#6CBF43", Warning: "#EBA400", Error: "#E65050", Info: "#22A4E6",
        Selection: "rgba(3, 91, 214, 0.15)",
        DiffAddedBg: "rgba(108, 191, 67, 0.13)", DiffRemovedBg: "rgba(230, 80, 80, 0.12)",
        Syntax: new ThemeSyntax(
            Comment: "#ADAEB1", Keyword: "#FA8532", String: "#86B300", Number: "#A37ACC",
            Function: "#EBA400", Type: "#22A4E6", Variable: "#5C6166", Operator: "#F2A191"));
}
