using System.Text.Json.Serialization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 백엔드 무관 PPTX 디자인 시스템. 테마 프리셋 × 시맨틱 레이아웃 → 추상 씬(Scene) op 리스트.
/// 이 클래스는 <b>어느 렌더러도 참조하지 않는다</b>(Open XML SDK·COM 타입 없음). 두 렌더러가 이 씬을 그린다:
///  - Open XML(<see cref="PptxCreateTool"/>): 파일 생성(전 플랫폼, PowerPoint 불필요).
///  - COM(MoaiCode.Tools.Office.PowerPointDesignRenderer): 열린 PowerPoint 에 실시간 디자인 슬라이드 추가(Windows).
/// 디자인(색·타이포·정렬·카드/스탯/프로세스 배치)은 여기 한 곳에만 있어 두 프런트가 표류하지 않는다(DRY).
/// </summary>
public static class PptxDesign
{
    // ── 슬라이드 기하(EMU). 12192000×6858000 = 16:9(와이드). 세로(H)는 4:3과 동일하므로
    //    세로 배치 상수는 그대로 두고 가로만 넓어진다 — 기존 레이아웃 수직 흐름 무영향. ──
    public const long SlideW = 12192000;     // 16:9 슬라이드 폭
    public const long SlideH = 6858000;      // 슬라이드 높이
    public const long LeftBarW = 110000;     // 좌측 accent 세로 바 폭
    public const long MarginX = 685800;      // 0.75"
    public const long ContentW = 10820400;   // 슬라이드 폭 - 좌우 여백(16:9)
    public const long BodyTop = 1500000;
    public const long BodyBottom = 6500000;
    public const long RowHeight = 370840;    // 표 행 높이 ≈ 0.4"
    public const long MinRowHeight = 210000; // 최소 행 높이 ≈ 0.23"(auto-fit 하한)
    public const long EmuPerInch = 914400;

    // 불릿 개수가 많을수록 시작 폰트를 줄여 슬라이드 밖으로 넘치는 것을 완화(autofit 과 병행).
    public static int BulletFontSize(int count) => count switch
    {
        <= 5 => 1800,
        <= 8 => 1600,
        <= 11 => 1400,
        <= 15 => 1200,
        _ => 1050,
    };

    // 불릿 수가 적을수록 문단 앞 여백(spaceBefore, %)을 키워 세로로 고르게 퍼뜨린다.
    public static int BulletSpaceBefore(int count) => count switch
    {
        <= 3 => 160000,  // 160%
        <= 4 => 120000,  // 120%
        <= 5 => 80000,   // 80%
        <= 6 => 45000,   // 45%
        _ => 20000,      // 20%
    };

    // ── 디자인 템플릿(프리셋) — 색·폰트·타이포 계층·정렬을 규격으로 고정. ──
    public sealed record ThemePreset(
        string Name,
        string BgHex,        // 슬라이드 배경
        string AccentHex,    // 강조(제목·바)
        string TitleHex,     // 제목 색
        string SubtitleHex,  // 부제 색
        string BodyHex,      // 본문 색
        string TitleFont,    // 제목 글꼴
        string BodyFont,     // 본문 글꼴
        int TitlePt,         // 제목 pt
        int SubtitlePt,      // 부제 pt
        int BodyPt,          // 본문 pt
        string PanelHex,     // 카드/패널 배경(배경보다 살짝 대비)
        string FooterHex,    // 푸터(페이지번호·구분선) 무채색
        string BorderHex);   // 카드 테두리(얇은 선)

    // A형: 연그레이 배경 + 네이비 accent + 흰 카드(얇은 테두리).
    public static readonly ThemePreset TemplateA = new(
        Name: "A", BgHex: "F7F8FA", AccentHex: "2F5496", TitleHex: "1F3864",
        SubtitleHex: "44546A", BodyHex: "333333",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 30, SubtitlePt: 17, BodyPt: 15,
        PanelHex: "FFFFFF", FooterHex: "AAB0BC", BorderHex: "E2E6EC");

    // B형: 흰 배경 + 큰 강조 타이포(키노트풍) + 레드 accent.
    public static readonly ThemePreset TemplateB = new(
        Name: "B", BgHex: "FFFFFF", AccentHex: "C00000", TitleHex: "C00000",
        SubtitleHex: "595959", BodyHex: "262626",
        TitleFont: "Arial", BodyFont: "Arial",
        TitlePt: 34, SubtitlePt: 18, BodyPt: 16,
        PanelHex: "FCFCFC", FooterHex: "B3B3B3", BorderHex: "EAEAEA");

    // C형: 미니멀 — 흰 배경 + 흰 카드(얇은 테두리) + 절제된 틸 accent(무채색 탈피). 여백 큰.
    public static readonly ThemePreset TemplateC = new(
        Name: "C", BgHex: "FBFBFA", AccentHex: "0E7C86", TitleHex: "14181C",
        SubtitleHex: "7A828A", BodyHex: "353A40",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 30, SubtitlePt: 16, BodyPt: 15,
        PanelHex: "FFFFFF", FooterHex: "B4BAC0", BorderHex: "E6E8E6");

    // D형: 다크(짙은 남색 배경·밝은 텍스트·시안 강조 포인트).
    public static readonly ThemePreset TemplateD = new(
        Name: "D", BgHex: "1F2430", AccentHex: "4FC3F7", TitleHex: "FFFFFF",
        SubtitleHex: "AEB6C7", BodyHex: "E3E8F0",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 32, SubtitlePt: 17, BodyPt: 15,
        PanelHex: "2A3242", FooterHex: "5A6478", BorderHex: "3A445A");

    public static ThemePreset ResolveTemplate(string? t) => t?.Trim().ToUpperInvariant() switch
    {
        "B" => TemplateB,
        "C" => TemplateC,
        "D" => TemplateD,
        _ => TemplateA, // 기본 A형
    };

    // 업로드 템플릿에서 추출한 브랜드 값으로 프리셋을 구성한다(코드 프리셋 대신 데이터-테마).
    // base(기본 A형) 위에 null 아닌 값만 덮어써, 미검출 필드는 안전한 기본값을 유지한다.
    // 색은 "#RRGGBB"/"RRGGBB" 모두 허용(내부는 '#' 없는 6자리). 폰트는 Latin 이름 사용.
    public static ThemePreset FromTheme(TemplateTheme t)
    {
        var b = TemplateA;
        string Hex(string? v, string fallback)
        {
            if (string.IsNullOrWhiteSpace(v))
            {
                return fallback;
            }

            var h = v.Trim().TrimStart('#').ToUpperInvariant();
            return h.Length == 6 && h.All(Uri.IsHexDigit) ? h : fallback;
        }

        string Font(string? latin, string? ea, string fallback) =>
            !string.IsNullOrWhiteSpace(latin) ? latin!
            : !string.IsNullOrWhiteSpace(ea) ? ea!
            : fallback;

        var accent = Hex(t.AccentHex, b.AccentHex);
        var text = Hex(t.TextHex, b.BodyHex);
        return b with
        {
            Name = "custom",
            BgHex = Hex(t.BgHex, b.BgHex),
            AccentHex = accent,
            TitleHex = text,        // 브랜드 텍스트색을 제목에 사용(가독성 우선), accent 는 바/포인트에.
            BodyHex = text,
            TitleFont = Font(t.TitleFontLatin, t.TitleFontEa, b.TitleFont),
            BodyFont = Font(t.BodyFontLatin, t.BodyFontEa, b.BodyFont),
        };
    }

    // ── 콘텐츠 스펙(JSON 바인딩 공용) — PptxCreate·PowerPointEdit 가 같은 스키마를 공유. ──
    public sealed record ColumnSpec(
        [property: JsonPropertyName("heading")] string? Heading,
        [property: JsonPropertyName("bullets")] List<string>? Bullets);

    public sealed record TableSpec(
        [property: JsonPropertyName("headers")] List<string>? Headers,
        [property: JsonPropertyName("rows")] List<List<string>>? Rows);

    public sealed record MetricSpec(
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("label")] string? Label);

    public sealed record CardSpec(
        [property: JsonPropertyName("heading")] string? Heading,
        [property: JsonPropertyName("body")] string? Body);

    public sealed record StepSpec(
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("caption")] string? Caption);

    public sealed record ShapeSpec(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("y")] double? Y,
        [property: JsonPropertyName("w")] double? W,
        [property: JsonPropertyName("h")] double? H,
        [property: JsonPropertyName("fill")] string? Fill,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("fontColor")] string? FontColor,
        [property: JsonPropertyName("fontSize")] int? FontSize,
        [property: JsonPropertyName("bold")] bool? Bold);

    public sealed record ImageSpec(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("y")] double? Y,
        [property: JsonPropertyName("widthInches")] double? WidthInches);

    public sealed record SlideSpec(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("layout")] string? Layout,
        [property: JsonPropertyName("accent")] string? Accent,
        [property: JsonPropertyName("bullets")] List<string>? Bullets,
        [property: JsonPropertyName("columns")] List<ColumnSpec>? Columns,
        [property: JsonPropertyName("table")] TableSpec? Table,
        [property: JsonPropertyName("metrics")] List<MetricSpec>? Metrics,
        [property: JsonPropertyName("cards")] List<CardSpec>? Cards,
        [property: JsonPropertyName("steps")] List<StepSpec>? Steps,
        [property: JsonPropertyName("shapes")] List<ShapeSpec>? Shapes,
        [property: JsonPropertyName("image")] ImageSpec? Image);

    // ── 씬 op(추상 프리미티브) — 렌더러가 각자 백엔드로 그린다. ──
    public enum Align { Left, Center, Right }
    public enum Anchor { Top, Center }

    public abstract record SceneOp;

    /// <summary>채운 사각형(테두리 없음): 배경·강조바.</summary>
    public sealed record BarOp(long X, long Y, long W, long H, string FillHex) : SceneOp;

    /// <summary>둥근 사각형 + 얇은 테두리: 카드/패널.</summary>
    public sealed record PanelOp(long X, long Y, long W, long H, string FillHex, string BorderHex) : SceneOp;

    /// <summary>채운 원 + 중앙 텍스트: 프로세스 단계 배지.</summary>
    public sealed record EllipseOp(long X, long Y, long D, string FillHex, string Text, int FontPt, string FontColorHex) : SceneOp;

    /// <summary>텍스트 상자(문단들). AutoFit=넘치면 폰트 자동 축소.</summary>
    public sealed record TextOp(string Name, long X, long Y, long W, long H, IReadOnlyList<ParaSpec> Paras, Anchor Anchor = Anchor.Top) : SceneOp;

    /// <summary>표(헤더 accent + 가로줄). availHeight 로 행 높이·폰트 적응.</summary>
    public sealed record TableOp(long X, long Y, long W, List<string> Headers, List<List<string>> Rows, string AccentHex, long AvailHeight) : SceneOp;

    /// <summary>자유 도형(다이어그램용, 인치 좌표). PptxCreate·COM 이 각자 그린다.</summary>
    public sealed record FreeShapeOp(ShapeSpec Spec) : SceneOp;

    /// <summary>한 문단의 서식 — CenteredText/AlignedText/TextParagraph 를 통합.</summary>
    public sealed record ParaSpec(
        string Text,
        int FontHundredths,   // pt*100
        bool Bold,
        bool Bullet,
        string? ColorHex,
        string? FontName,
        Align Align = Align.Left,
        int SpaceBeforePct = 0);

    private const string White = "FFFFFF";

    // 시맨틱 레이아웃 × 프리셋 → 씬 op 리스트. (BuildSlide 이식: 순서·좌표·색 그대로.)
    public static List<SceneOp> Plan(SlideSpec s, ThemePreset p, int index, int total)
    {
        var ops = new List<SceneOp>();

        // 공통 디자인: 배경 + 좌측 accent 세로 바.
        ops.Add(new BarOp(0, 0, SlideW, SlideH, p.BgHex));
        ops.Add(new BarOp(0, 0, LeftBarW, SlideH, p.AccentHex));

        var layout = (s.Layout ?? "content").Trim().ToLowerInvariant();

        // ── cover: 중앙 큰 제목 + 부제 ──
        if (layout == "cover")
        {
            ops.Add(new TextOp("Title", MarginX, 2600000, ContentW, 1200000,
                new[] { Centered(s.Title ?? string.Empty, p.TitlePt + 12, true, p.TitleHex, p.TitleFont) }));
            ops.Add(new BarOp((SlideW - 1400000) / 2, 3860000, 1400000, 44000, p.AccentHex));
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
            {
                ops.Add(new TextOp("Subtitle", MarginX, 4000000, ContentW, 700000,
                    new[] { Centered(s.Subtitle!, p.SubtitlePt, false, p.SubtitleHex, p.BodyFont) }));
            }

            AppendFreeShapes(ops, s);
            return ops;
        }

        // ── section: 구간 구분(좌측 강조 블록 + 큰 제목 + 부제) ──
        if (layout == "section")
        {
            ops.Add(new BarOp(MarginX, 2550000, 300000, 1000000, p.AccentHex));
            ops.Add(new TextOp("Title", MarginX + 480000, 2660000, ContentW - 480000, 880000,
                new[] { Para(s.Title ?? string.Empty, (p.TitlePt + 6) * 100, bold: true, bullet: false, color: p.TitleHex, fontName: p.TitleFont) }));
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
            {
                ops.Add(new TextOp("Subtitle", MarginX + 480000, 3560000, ContentW - 480000, 480000,
                    new[] { Para(s.Subtitle!, p.SubtitlePt * 100, bold: false, bullet: false, color: p.SubtitleHex, fontName: p.BodyFont) }));
            }

            AppendFreeShapes(ops, s);
            return ops;
        }

        // ── quote: 큰 문구 하나(중앙) ──
        if (layout == "quote")
        {
            ops.Add(new TextOp("Quote", MarginX + 300000, 2300000, ContentW - 600000, 2200000,
                new[] { Centered(s.Title ?? s.Subtitle ?? string.Empty, p.SubtitlePt + 12, true, p.TitleHex, p.TitleFont) }));
            ops.Add(new BarOp((SlideW - 1400000) / 2, 4650000, 1400000, 44000, p.AccentHex));
            AppendFreeShapes(ops, s);
            return ops;
        }

        // ── content / text_image 공통: 좌측 정렬 제목 + 부제 + 짧은 강조바 ──
        long bodyTop = BodyTop;
        if (!string.IsNullOrWhiteSpace(s.Title))
        {
            ops.Add(new TextOp("Title", MarginX, 360000, ContentW, 720000,
                new[] { Para(s.Title!, p.TitlePt * 100, bold: true, bullet: false, color: p.TitleHex, fontName: p.TitleFont) }));
            long y = 1080000;
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
            {
                ops.Add(new TextOp("Subtitle", MarginX, y, ContentW, 440000,
                    new[] { Para(s.Subtitle!, p.SubtitlePt * 100, bold: false, bullet: false, color: p.SubtitleHex, fontName: p.BodyFont) }));
                y += 470000;
            }

            ops.Add(new BarOp(MarginX, y + 30000, 820000, 42000, p.AccentHex));
            bodyTop = y + 250000;
        }

        var hasCols = s.Columns is { Count: > 0 };
        var hasBullets = s.Bullets is { Count: > 0 };
        var hasTable = s.Table is not null && ((s.Table.Headers?.Count ?? 0) > 0 || (s.Table.Rows?.Count ?? 0) > 0);
        var hasMetrics = s.Metrics is { Count: > 0 };
        var hasCards = s.Cards is { Count: > 0 };
        var hasSteps = s.Steps is { Count: > 0 };

        // 본문 폭: text_image 는 좌측 절반(우측은 이미지 자리).
        long bodyW = layout == "text_image" ? (SlideW / 2) - MarginX : ContentW;
        long bodyH = BodyBottom - bodyTop;

        if (hasCols)
        {
            var cols = s.Columns!.Take(2).ToList();
            const long gap = 360000;
            var colW = (ContentW - gap) / 2;
            const long pad = 260000;              // 카드 안쪽 여백 ≈ 0.28"
            var cardH = BodyBottom - bodyTop;
            for (var i = 0; i < cols.Count; i++)
            {
                var x = MarginX + i * (colW + gap);
                ops.Add(new PanelOp(x, bodyTop, colW, cardH, p.PanelHex, p.BorderHex));
                ops.Add(new BarOp(x + pad, bodyTop + pad, 300000, 46000, p.AccentHex));

                // 모델이 heading 에 여러 줄을 \n 으로 뭉쳐 보내면 전부 헤딩 서식(굵은 강조색)이 되어
                // "본문이 제목 서식으로 붕괴"처럼 보인다(2026-08-26 재발). 첫 줄만 헤딩, 나머지는 불릿으로.
                var (heading, extra) = SplitHeading(cols[i].Heading);
                var bullets = extra.Concat(cols[i].Bullets ?? new List<string>()).ToList();
                var colSize = Math.Min(p.BodyPt * 100, BulletFontSize(bullets.Count));
                var colGap = BulletSpaceBefore(bullets.Count);
                var paras = new List<ParaSpec>();
                if (!string.IsNullOrWhiteSpace(heading))
                {
                    paras.Add(Para(heading!, p.SubtitlePt * 100, bold: true, bullet: false, color: p.AccentHex, fontName: p.BodyFont));
                }

                foreach (var b in bullets)
                {
                    paras.Add(Para(b, colSize, bold: false, bullet: true, color: p.BodyHex, fontName: p.BodyFont, spaceBeforePct: colGap));
                }

                if (paras.Count == 0)
                {
                    paras.Add(Para(string.Empty, p.BodyPt * 100, false, false, null));
                }

                ops.Add(new TextOp($"Col{i + 1}", x + pad, bodyTop + pad + 120000, colW - 2 * pad, cardH - 2 * pad - 120000, paras));
            }
        }
        else if (hasBullets)
        {
            var size = Math.Min(p.BodyPt * 100, BulletFontSize(s.Bullets!.Count));
            var gap = BulletSpaceBefore(s.Bullets!.Count);
            var paras = s.Bullets!.Select(b => Para(b, size, bold: false, bullet: true, color: p.BodyHex, fontName: p.BodyFont, spaceBeforePct: gap)).ToList();

            if (layout == "text_image")
            {
                ops.Add(new TextOp("Body", MarginX, bodyTop, bodyW, bodyH, paras));
            }
            else
            {
                const long pad = 320000;      // 카드 안쪽 여백 ≈ 0.35"
                ops.Add(new PanelOp(MarginX, bodyTop, ContentW, bodyH, p.PanelHex, p.BorderHex));
                ops.Add(new BarOp(MarginX + pad, bodyTop + pad, 300000, 46000, p.AccentHex));
                ops.Add(new TextOp("Body", MarginX + pad, bodyTop + pad + 130000, ContentW - 2 * pad, bodyH - 2 * pad - 130000, paras));
            }
        }
        else if (hasMetrics)
        {
            BuildStatRow(ops, bodyTop, bodyH, s.Metrics!, p);
        }
        else if (hasCards)
        {
            BuildCardGrid(ops, bodyTop, bodyH, s.Cards!, p);
        }
        else if (hasSteps)
        {
            BuildProcess(ops, bodyTop, bodyH, s.Steps!, p);
        }

        if (hasTable)
        {
            ops.Add(new TableOp(MarginX, bodyTop, ContentW,
                s.Table!.Headers ?? new List<string>(), s.Table.Rows ?? new List<List<string>>(),
                p.AccentHex, BodyBottom - bodyTop));
        }

        AppendFreeShapes(ops, s);

        // 콘텐츠 계열 슬라이드 하단 푸터(구분선 + 페이지 번호).
        AppendFooter(ops, index, total, p);
        return ops;
    }

    private static void AppendFreeShapes(List<SceneOp> ops, SlideSpec s)
    {
        if (s.Shapes is { Count: > 0 })
        {
            foreach (var sh in s.Shapes!)
            {
                ops.Add(new FreeShapeOp(sh));
            }
        }
    }

    // ── stat: 2~4개의 큰 수치 콜아웃(KPI). 배경 카드 + 큰 값(accent) + 라벨. 세로 중앙 정렬. ──
    private static void BuildStatRow(List<SceneOp> ops, long bodyTop, long bodyH, List<MetricSpec> metrics, ThemePreset p)
    {
        var items = metrics.Take(4).ToList();
        var n = items.Count;
        const long gap = 360000;
        var cardW = (ContentW - gap * (n - 1)) / n;
        const long cardH = 2100000;
        var cy = Math.Max(bodyTop, bodyTop + (bodyH - cardH) / 2);
        for (var i = 0; i < n; i++)
        {
            var x = MarginX + i * (cardW + gap);
            ops.Add(new PanelOp(x, cy, cardW, cardH, p.PanelHex, p.BorderHex));
            // 값이 길면("90분 → 5분" 등) 44pt 한 줄이 카드 폭을 넘어 두 줄로 꺾여 라벨 위로 내려앉는다(2026-08-26 사고).
            // 카드 안쪽 폭에 맞춰 폰트를 자동 축소(44→최소 24pt). 값 박스 높이(900000)는 44pt 한 줄 기준이므로 그대로.
            var value = items[i].Value ?? string.Empty;
            var valuePt = FitFontPt(value, cardW - 2 * 180000, 44, 24);
            ops.Add(new TextOp("Stat", x, cy + 320000, cardW, 900000,
                new[] { Centered(value, valuePt, true, p.AccentHex, p.TitleFont) }));
            ops.Add(new TextOp("StatLabel", x + 180000, cy + 1300000, cardW - 360000, 640000,
                new[] { Centered(items[i].Label ?? string.Empty, p.SubtitlePt, false, p.SubtitleHex, p.BodyFont) }));
        }
    }

    // 한 줄 텍스트가 boxW(EMU) 안에 들어가는 최대 폰트(pt)를 startPt 부터 2pt 씩 내려가며 찾는다(minPt 하한).
    // 글자 폭 추정: CJK·기호(→ 등 비-ASCII) 1.0em, ASCII 대문자/숫자 0.6em, 그 외 ASCII 0.5em, 공백 0.3em (볼드 여유 포함 근사).
    public static int FitFontPt(string text, long boxW, int startPt, int minPt)
    {
        double em = 0;
        foreach (var ch in text)
        {
            em += ch switch
            {
                ' ' => 0.3,
                _ when ch > 0x7F => 1.0,
                _ when char.IsUpper(ch) || char.IsDigit(ch) => 0.6,
                _ => 0.5,
            };
        }

        for (var pt = startPt; pt > minPt; pt -= 2)
        {
            if (em * pt * 12700 <= boxW)
            {
                return pt;
            }
        }

        return minPt;
    }

    // ── cards: 2~4개의 타일(요점 카드). 4개는 2×2 그리드. 각 카드 = 패널 + accent 칩 + 헤딩 + 본문. ──
    // heading 문자열에서 첫 줄만 헤딩으로, 나머지 줄은 본문/불릿 후보로 분리한다(빈 줄 제거).
    private static (string? Heading, List<string> Extra) SplitHeading(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return (heading, new List<string>());
        }

        var lines = heading.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return lines.Count == 0 ? (null, new List<string>()) : (lines[0], lines.Skip(1).ToList());
    }

    private static void BuildCardGrid(List<SceneOp> ops, long bodyTop, long bodyH, List<CardSpec> cards, ThemePreset p)
    {
        var items = cards.Take(4).ToList();
        var n = items.Count;
        var cols = n <= 3 ? n : 2;
        var rows = (n + cols - 1) / cols;
        const long gap = 300000;
        const long pad = 240000;
        const long maxCardH = 1950000; // 내용(헤딩+1~2줄) 대비 과도한 카드 높이 방지.
        var cardW = (ContentW - gap * (cols - 1)) / cols;
        var cardH = Math.Min(maxCardH, (bodyH - gap * (rows - 1)) / rows);
        var gridH = cardH * rows + gap * (rows - 1);
        var gridTop = bodyTop + Math.Max(0, (bodyH - gridH) / 2); // 그리드를 본문 영역에 세로 중앙.
        for (var i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            var x = MarginX + c * (cardW + gap);
            var y = gridTop + r * (cardH + gap);
            ops.Add(new PanelOp(x, y, cardW, cardH, p.PanelHex, p.BorderHex));
            ops.Add(new BarOp(x + pad, y + pad, 300000, 46000, p.AccentHex));
            var paras = new List<ParaSpec>();
            var (heading, extra) = SplitHeading(items[i].Heading); // 개행 뭉침 방어(two_col 과 동일)
            if (!string.IsNullOrWhiteSpace(heading))
            {
                paras.Add(Para(heading!, p.SubtitlePt * 100, bold: true, bullet: false, color: p.AccentHex, fontName: p.BodyFont));
            }

            var body = string.Join("\n", extra.Append(items[i].Body ?? string.Empty).Where(t => !string.IsNullOrWhiteSpace(t)));
            if (!string.IsNullOrWhiteSpace(body))
            {
                paras.Add(Para(body, p.BodyPt * 100, bold: false, bullet: false, color: p.BodyHex, fontName: p.BodyFont, spaceBeforePct: 45000));
            }

            if (paras.Count == 0)
            {
                paras.Add(Para(string.Empty, p.BodyPt * 100, false, false, null));
            }

            ops.Add(new TextOp("Card", x + pad, y + pad + 120000, cardW - 2 * pad, cardH - 2 * pad - 120000, paras));
        }
    }

    // ── process: 2~5개 단계의 가로 흐름. 각 단계 = 패널 + 번호 배지(원) + 라벨 + 캡션, 사이에 › 화살표. ──
    private static void BuildProcess(List<SceneOp> ops, long bodyTop, long bodyH, List<StepSpec> steps, ThemePreset p)
    {
        var items = steps.Take(5).ToList();
        var n = items.Count;
        const long gap = 220000;
        const long stepH = 2300000;
        const long badge = 620000;
        var stepW = (ContentW - gap * (n - 1)) / n;
        var cy = Math.Max(bodyTop, bodyTop + (bodyH - stepH) / 2);
        for (var i = 0; i < n; i++)
        {
            var x = MarginX + i * (stepW + gap);
            ops.Add(new PanelOp(x, cy, stepW, stepH, p.PanelHex, p.BorderHex));
            ops.Add(new EllipseOp(x + (stepW - badge) / 2, cy + 230000, badge, p.AccentHex, (i + 1).ToString(), 22, White));
            ops.Add(new TextOp("StepLabel", x + 120000, cy + 230000 + badge + 70000, stepW - 240000, 520000,
                new[] { Centered(items[i].Label ?? string.Empty, p.SubtitlePt, true, p.TitleHex, p.BodyFont) }));
            if (!string.IsNullOrWhiteSpace(items[i].Caption))
            {
                ops.Add(new TextOp("StepCap", x + 120000, cy + 230000 + badge + 640000, stepW - 240000, 760000,
                    new[] { Centered(items[i].Caption!, Math.Max(10, p.BodyPt - 1), false, p.BodyHex, p.BodyFont) }));
            }

            if (i < n - 1)
            {
                ops.Add(new TextOp("Arrow", x + stepW - 40000, cy + (stepH / 2) - 220000, gap + 80000, 440000,
                    new[] { Centered("›", 28, true, p.AccentHex, p.BodyFont) }));
            }
        }
    }

    // 하단 푸터: 얇은 구분선 + "n / N" 페이지 번호(무채색).
    private static void AppendFooter(List<SceneOp> ops, int index, int total, ThemePreset p)
    {
        const long footY = 6480000;
        ops.Add(new BarOp(MarginX, footY, 460000, 26000, p.AccentHex));
        ops.Add(new TextOp("PageNo", SlideW - MarginX - 900000, footY - 120000, 900000, 300000,
            new[] { new ParaSpec($"{index} / {total}", 11 * 100, false, false, p.FooterHex, p.BodyFont, Align.Right) }));
    }

    // 중앙 정렬 단일 문단(제목·라벨·배지 등). pt 단위 입력 → 내부에서 hundredths 로.
    private static ParaSpec Centered(string text, int fontPt, bool bold, string? color, string? fontName = null) =>
        new(text, fontPt * 100, bold, false, color, fontName, Align.Center);

    // 좌측 정렬(불릿/비불릿) 단일 문단. fontSize 는 이미 hundredths.
    private static ParaSpec Para(string text, int fontSize, bool bold, bool bullet, string? color, string? fontName = null, int spaceBeforePct = 0) =>
        new(text, fontSize, bold, bullet, color, fontName, Align.Left, spaceBeforePct);
}
