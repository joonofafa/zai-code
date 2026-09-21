using DocumentFormat.OpenXml.Packaging;
using D = DocumentFormat.OpenXml.Drawing;

namespace MoaiCode.Tools.OpenXml;

/// <summary>템플릿에서 뽑은 브랜드 테마 값(색·폰트). 생성 시 이 값으로 스타일링한다(코드 프리셋 대체).</summary>
public sealed record TemplateTheme(
    string? AccentHex,   // 주 강조색 (accent1)
    string? BgHex,       // 배경 (light1)
    string? TextHex,     // 본문 텍스트 (dark1)
    string? TitleFontLatin,
    string? TitleFontEa, // East Asian(한글 등)
    string? BodyFontLatin,
    string? BodyFontEa);

/// <summary>템플릿 .pptx 분석 결과 — 마스터 테마(색·폰트) + 슬라이드 수.</summary>
public sealed record TemplateAnalysis(int SlideCount, TemplateTheme Theme);

/// <summary>
/// 템플릿 .pptx 를 OpenXML 로 읽어(PowerPoint COM 불필요) 브랜드 테마(색·폰트)를 추출한다.
/// 추출값은 SKILL.md 에 데이터로 실려 생성 시 스타일링에 쓰인다. 마스터 아티팩트 자체는 원본 파일.
/// </summary>
public static class TemplateAnalyzer
{
    // 확장자로 분기해 브랜드 테마를 추출한다. pptx/docx/xlsx 모두 OOXML theme part 를 공유한다.
    public static TemplateAnalysis? Analyze(string path)
    {
        try
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".pptx" => AnalyzePptx(path),
                ".docx" => AnalyzeGeneric(WordprocessingDocument.Open(path, false).MainDocumentPart?.ThemePart?.Theme),
                ".xlsx" => AnalyzeGeneric(SpreadsheetDocument.Open(path, false).WorkbookPart?.ThemePart?.Theme),
                _ => null,
            };
        }
        catch
        {
            return null; // 손상/암호화/비표준 → 분석 없음(등록은 계속)
        }
    }

    private static TemplateAnalysis? AnalyzePptx(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var pp = doc.PresentationPart;
        if (pp is null)
        {
            return null;
        }

        var slideCount = pp.SlideParts.Count();
        var theme = ExtractTheme(pp.ThemePart?.Theme ?? pp.SlideMasterParts.FirstOrDefault()?.ThemePart?.Theme);

        // 브랜드 accent 보정: 테마 accent1 이 기본 파랑 등 브랜드와 다를 수 있으므로,
        // 슬라이드에서 실제로 가장 많이 쓰인 채도 있는 색(지배색)을 우선한다.
        var dominant = DominantAccent(pp);
        if (dominant is not null)
        {
            theme = theme with { AccentHex = dominant };
        }

        return new TemplateAnalysis(slideCount, theme);
    }

    // docx/xlsx: theme part 만으로 색·폰트 추출(지배색 스캔 없음 — 이들은 accent1 이 브랜드색인 경우가 일반적).
    private static TemplateAnalysis? AnalyzeGeneric(D.Theme? theme)
    {
        if (theme is null)
        {
            return null;
        }

        return new TemplateAnalysis(0, ExtractTheme(theme));
    }

    // theme part 에서 색·폰트를 뽑는다(모든 OOXML 공통).
    private static TemplateTheme ExtractTheme(D.Theme? theme)
    {
        var els = theme?.ThemeElements;

        var clr = els?.ColorScheme;
        var accent = Hex(clr?.Accent1Color);
        var bg = Hex(clr?.Light1Color);
        var text = Hex(clr?.Dark1Color);

        var font = els?.FontScheme;
        var (titleLatin, titleEa) = FontNames(font?.MajorFont);
        var (bodyLatin, bodyEa) = FontNames(font?.MinorFont);

        return new TemplateTheme(accent, bg, text, titleLatin, titleEa, bodyLatin, bodyEa);
    }

    // 슬라이드에서 실제로 가장 많이 쓰인 브랜드색을 찾는다. 직접색(srgbClr) + 테마참조(schemeClr accent1~6)를
    // 모두 세고, 흰/검/회색(저채도)은 제외. 브랜드 accent 가 테마 accent1 슬롯에 없을 때(예: BC 빨강) 대비.
    private static string? DominantAccent(PresentationPart pp)
    {
        // accent1~6 스킴색 → 실제 hex 매핑(schemeClr 참조 해석용).
        var els = (pp.ThemePart?.Theme ?? pp.SlideMasterParts.FirstOrDefault()?.ThemePart?.Theme)?.ThemeElements;
        var scheme = els?.ColorScheme;
        var accentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["accent1"] = Hex(scheme?.Accent1Color) ?? "",
            ["accent2"] = Hex(scheme?.Accent2Color) ?? "",
            ["accent3"] = Hex(scheme?.Accent3Color) ?? "",
            ["accent4"] = Hex(scheme?.Accent4Color) ?? "",
            ["accent5"] = Hex(scheme?.Accent5Color) ?? "",
            ["accent6"] = Hex(scheme?.Accent6Color) ?? "",
        };

        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Bump(string? hex)
        {
            if (!string.IsNullOrEmpty(hex) && IsBrandColor(hex))
            {
                freq[hex] = freq.GetValueOrDefault(hex) + 1;
            }
        }

        foreach (var sp in pp.SlideParts)
        {
            foreach (var fill in sp.Slide.Descendants<D.SolidFill>())
            {
                var srgb = fill.GetFirstChild<D.RgbColorModelHex>()?.Val?.Value;
                if (!string.IsNullOrEmpty(srgb))
                {
                    Bump("#" + srgb.ToUpperInvariant());
                    continue;
                }

                var sch = fill.GetFirstChild<D.SchemeColor>()?.Val;
                if (sch is not null && accentMap.TryGetValue(sch.InnerText, out var mapped) && mapped.Length > 0)
                {
                    Bump(mapped);
                }
            }
        }

        // 가장 빈번한 브랜드색. 동률이면 채도 높은 쪽. 없으면 null(테마 accent1 유지).
        return freq.Count == 0 ? null
            : freq.OrderByDescending(kv => kv.Value).ThenByDescending(kv => Saturation(kv.Key)).First().Key;
    }

    // 브랜드색 후보: 흰/검/저채도 회색 제외(배경·텍스트·구분선 걸러냄).
    private static bool IsBrandColor(string hex)
    {
        if (!TryRgb(hex, out var r, out var g, out var b))
        {
            return false;
        }

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max >= 0xE6 && min >= 0xE6)
        {
            return false; // 흰색 계열
        }

        if (max <= 0x28)
        {
            return false; // 검정 계열
        }

        return (max - min) >= 0x28; // 채도(색상 차이) 충분해야 브랜드색
    }

    private static int Saturation(string hex)
    {
        if (!TryRgb(hex, out var r, out var g, out var b))
        {
            return 0;
        }

        return Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
    }

    private static bool TryRgb(string hex, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var h = hex.TrimStart('#');
        if (h.Length != 6 || !h.All(Uri.IsHexDigit))
        {
            return false;
        }

        r = Convert.ToInt32(h[..2], 16);
        g = Convert.ToInt32(h[2..4], 16);
        b = Convert.ToInt32(h[4..6], 16);
        return true;
    }

    // Color2Type(dk1/lt1/accent1…) → "#RRGGBB". srgbClr 우선, sysClr 은 lastClr 폴백. 없으면 null.
    private static string? Hex(D.Color2Type? c)
    {
        if (c is null)
        {
            return null;
        }

        var srgb = c.GetFirstChild<D.RgbColorModelHex>()?.Val?.Value;
        if (!string.IsNullOrEmpty(srgb))
        {
            return "#" + srgb.ToUpperInvariant();
        }

        var sys = c.GetFirstChild<D.SystemColor>()?.LastColor?.Value;
        return string.IsNullOrEmpty(sys) ? null : "#" + sys.ToUpperInvariant();
    }

    // MajorFont/MinorFont → (Latin, EastAsian) typeface. 비어 있으면 null.
    private static (string? Latin, string? Ea) FontNames(D.FontCollectionType? f)
    {
        if (f is null)
        {
            return (null, null);
        }

        var latin = f.LatinFont?.Typeface?.Value;
        var ea = f.EastAsianFont?.Typeface?.Value;
        return (
            string.IsNullOrWhiteSpace(latin) ? null : latin,
            string.IsNullOrWhiteSpace(ea) ? null : ea);
    }
}
