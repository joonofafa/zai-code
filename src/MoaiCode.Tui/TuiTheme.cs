namespace MoaiCode.Tui;

/// <summary>
/// TUI 색 테마의 단일 지점. 보조/희미한 텍스트(툴 호출·결과 미리보기·메타 정보)의 색을 한 곳에서 정한다.
/// 기본 <c>grey70</c> 은 256색을 16색으로 다운샘플하는 Windows 터미널(레거시 conhost 등)에서 어두운
/// 회색으로 뭉개져 잘 안 보인다. 그래서 프리셋으로 더 밝은/대비 높은 색을 고를 수 있게 한다.
///
/// 프리셋 값은 <b>16색 안전한</b> 이름을 쓴다(silver=7, white=15) — 다운샘플돼도 밝게 유지되도록.
/// 결정 순서: /theme(런타임) → 설정 theme → env MOAI_THEME → 기본 default. Cli 가 부트스트랩에서 <see cref="Apply"/>.
/// </summary>
public static class TuiTheme
{
    /// <summary>보조/희미한 텍스트 색(Spectre 마크업 이름). 마크업에 <c>[{TuiTheme.Dim}]…[/]</c> 로 끼운다.</summary>
    public static string Dim { get; private set; } = "grey70";

    /// <summary>현재 프리셋 이름(정규화됨). /theme 표시·설정 저장용.</summary>
    public static string Current { get; private set; } = "default";

    public static readonly IReadOnlyList<string> Presets = new[] { "default", "bright", "high-contrast" };

    /// <summary>프리셋 적용. 알 수 없는 이름은 default 로 폴백. env 초기화·/theme·부트스트랩이 호출.</summary>
    public static void Apply(string? name)
    {
        Current = Normalize(name);
        Dim = Current switch
        {
            // 실측: 256색은 grey70(249)<grey93(255), 16색은 grey70→37 vs grey93/white→97(bright white).
            // silver/grey85 는 16색에서 grey70 과 똑같이 37 로 떨어져 구분이 안 되므로 쓰지 않는다.
            "bright" => "grey93",        // 256:255 / 16:97 — 모든 모드에서 default 보다 밝음
            "high-contrast" => "white",  // 256:15  / 16:97
            _ => "grey70",               // default — 기존 동작 보존
        };
    }

    /// <summary>입력을 알려진 프리셋 이름으로 정규화. 모르는 값이면 "default".</summary>
    public static string Normalize(string? name)
    {
        var v = name?.Trim().ToLowerInvariant().Replace("_", "-");
        return v switch
        {
            "bright" => "bright",
            "high-contrast" or "highcontrast" or "contrast" => "high-contrast",
            _ => "default",
        };
    }

    /// <summary>env MOAI_THEME 로 초기화(설정→env 는 Cli 가 병합해 내보낸다). 미설정이면 default.</summary>
    public static void InitFromEnv()
        => Apply(Environment.GetEnvironmentVariable("MOAI_THEME"));
}
