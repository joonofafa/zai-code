using System.Collections.Concurrent;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 언어별 기본 폰트 매칭 + 설치 폰트 검증. 문서 텍스트의 스크립트(한글/일문/중문/라틴)를 감지해
/// 해당 언어의 선호 폰트 체인에서 **실제 설치된 첫 폰트**를 고른다(없으면 선호 1순위 그대로).
/// 설치 검증은 폰트 파일 존재로 판단(크로스타깃·무의존). 생성기(문서 만드는 머신)와 뷰어가 같은
/// Windows 데스크톱인 경우(=MoAI Desktop) 정확하고, 헤드리스/리눅스에서는 선호 1순위로 폴백한다.
/// </summary>
public static class FontResolver
{
    public const string LatinFont = "Arial";

    // 언어 → East Asian 선호 폰트 체인(앞이 1순위).
    private static readonly Dictionary<string, string[]> Chains = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ko"] = new[] { "Malgun Gothic", "NanumGothic", "Gulim", "Batang" },
        ["ja"] = new[] { "Yu Gothic", "Meiryo", "MS Gothic" },
        ["zh"] = new[] { "Microsoft YaHei", "SimSun", "SimHei" },
    };

    // 폰트 표시명 → Windows Fonts 폴더의 후보 파일명(설치 검증용).
    private static readonly Dictionary<string, string[]> Files = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Malgun Gothic"] = new[] { "malgun.ttf", "malgunsl.ttf" },
        ["NanumGothic"] = new[] { "NanumGothic.ttf", "NanumGothic.ttc" },
        ["Gulim"] = new[] { "gulim.ttc" },
        ["Batang"] = new[] { "batang.ttc" },
        ["Yu Gothic"] = new[] { "YuGothR.ttc", "YuGothM.ttc", "yugothic.ttf" },
        ["Meiryo"] = new[] { "meiryo.ttc" },
        ["MS Gothic"] = new[] { "msgothic.ttc" },
        ["Microsoft YaHei"] = new[] { "msyh.ttc", "msyh.ttf" },
        ["SimSun"] = new[] { "simsun.ttc" },
        ["SimHei"] = new[] { "simhei.ttf" },
    };

    private static readonly Lazy<string[]> FontDirs = new(() =>
    {
        var dirs = new List<string>();
        void Add(string? d)
        {
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
            {
                dirs.Add(d);
            }
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));       // %WINDIR%\Fonts
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            Add(Path.Combine(local, "Microsoft", "Windows", "Fonts"));         // 사용자 설치 폰트(Win10+)
        }

        return dirs.ToArray();
    });

    private static readonly ConcurrentDictionary<string, bool> InstalledCache = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsInstalled(string fontName)
    {
        return InstalledCache.GetOrAdd(fontName, name =>
        {
            if (!Files.TryGetValue(name, out var candidates))
            {
                return false; // 매핑 없는 폰트는 검증 불가 → 미설치 취급.
            }

            var dirs = FontDirs.Value;
            return dirs.Length > 0 && candidates.Any(f => dirs.Any(d => File.Exists(Path.Combine(d, f))));
        });
    }

    /// <summary>언어코드(ko/ja/zh)의 선호 체인에서 설치된 첫 폰트. 없으면 1순위 폰트명 그대로.</summary>
    public static string ResolveEastAsian(string lang)
    {
        if (!Chains.TryGetValue(lang, out var chain) || chain.Length == 0)
        {
            return "Malgun Gothic"; // 알 수 없는 언어는 한국어 기본으로.
        }

        return chain.FirstOrDefault(IsInstalled) ?? chain[0];
    }

    /// <summary>텍스트의 스크립트로 언어 추정. 한글 우선, 그다음 일문 가나, 그다음 한자(→중문). 라틴/기타는 null.</summary>
    public static string? DetectLang(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var hasHan = false;
        foreach (var c in text)
        {
            if (c is (>= '가' and <= '힣') or (>= 'ᄀ' and <= 'ᇿ') or (>= '㄰' and <= '㆏'))
            {
                return "ko"; // 한글 있으면 즉시 한국어.
            }

            if (c is (>= '぀' and <= 'ゟ') or (>= '゠' and <= 'ヿ'))
            {
                return "ja"; // 히라가나/가타카나 → 일본어.
            }

            if (c is >= '一' and <= '鿿')
            {
                hasHan = true; // 한자만 있으면 마지막에 중문으로.
            }
        }

        return hasHan ? "zh" : null;
    }

    /// <summary>텍스트에 맞는 EA 폰트(없으면 null = 라틴만). docx/pptx 런에서 사용.</summary>
    public static string? EastAsianFor(string? text) =>
        DetectLang(text) is { } lang ? ResolveEastAsian(lang) : null;

    /// <summary>언어코드가 동아시아(ko/ja/zh)면 그대로, 아니면 null. 앱 언어 → 문서 기본 폰트 결정용.</summary>
    public static string? EastAsianLangOrNull(string? lang) =>
        !string.IsNullOrWhiteSpace(lang) && Chains.ContainsKey(lang.Trim()) ? lang.Trim() : null;

    /// <summary>런 프루핑 언어 태그(BCP-47). EA 미감지 시 en-US.</summary>
    public static string BcpTag(string? lang) => lang switch
    {
        "ko" => "ko-KR",
        "ja" => "ja-JP",
        "zh" => "zh-CN",
        _ => "en-US",
    };

    /// <summary>
    /// 앱 언어(L10n)가 동아시아면 그 언어의 기본 폰트(맑은 고딕 등), 아니면 null.
    /// 생성 문서(pptx/docx/xlsx)의 기본 폰트를 언어에 맞춰 통일하는 데 쓴다(라틴 기본값 대체).
    /// </summary>
    public static string? AppDefaultEastAsianFont()
    {
        var lang = EastAsianLangOrNull(L10n.CurrentLanguage);
        return lang is null ? null : ResolveEastAsian(lang);
    }
}
