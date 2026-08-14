using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 새 PowerPoint 프레젠테이션(.pptx)을 만든다. Open XML SDK — PowerPoint 설치 불필요, 전 플랫폼.
/// 최소 유효 구조(presentation + slide master + slide layout + slides)를 직접 조립한다.
/// </summary>
public sealed class PptxCreateTool : ITool
{
    public string Name => "PptxCreate";

    public string Description => """
        Creates a new PowerPoint presentation (.pptx, 16:9) using the built-in Open XML writer — no
        dependencies, no PowerPoint install. DESIGN IS TEMPLATE-DRIVEN: pick a "template" (A/B/C/D) and a
        per-slide "layout"; the template fixes colors, fonts, typography hierarchy (title/subtitle/body),
        card framing, spacing and alignment, so you ONLY supply content. Do NOT hand-tune a "pretty"
        design — choose the right layout and write concise content instead.
        VARY THE LAYOUTS — a deck of only "content" bullet slides looks poor. Prefer visual layouts:
          - cover: title + subtitle (centered) — the opening slide
          - section: a divider between parts (title + subtitle)
          - content: title + subtitle + bullets. Keep bullets to <=4, each ONE short line.
          - two_col: comparison — provide "columns" (max 2; each heading + bullets)
          - stat: 2-4 big-number KPI callouts — provide "metrics" (each value + label). Use for figures.
          - cards: 2-4 key points as tiles instead of a bullet wall — provide "cards" (each heading + body).
          - process: 2-5 sequential steps as a numbered flow — provide "steps" (each label + optional caption).
          - text_image: explanation with a figure — bullets on the left + "image" on the right
          - table: data — provide "table" (headers + rows)
          - quote: one strong statement — put it in "title"
        Rule of thumb: numbers→stat, 3-4 ideas→cards, a sequence→process, a comparison→two_col,
        dense prose→content. Insert a "section" divider between major parts.
        "shapes" places free-form boxes/arrows (rect/roundRect/ellipse/arrow/chevron/diamond) at inch
        coordinates (slide is 13.33 x 7.5 (16:9)) for custom diagrams. ALWAYS use this tool to produce a
        .pptx. Do NOT install packages (pptxgenjs, python-pptx, etc.) or write scripts. For editing an OPEN
        presentation on Windows, use PowerPointEdit.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Output .pptx path (relative to workspace)" },
            "template": { "type": "string", "enum": ["A", "B", "C", "D"], "description": "Design template (default A). A = light corporate (grey bg, navy, left accent bar). B = keynote/bold (white bg, red, large type). C = minimal (white bg, black type, thin neutral bar, airy). D = dark (deep navy bg, light text, cyan accent). The template fixes colors, fonts, typography hierarchy, spacing and alignment — you only provide content." },
            "slides": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "subtitle": { "type": "string", "description": "Secondary line under the title (subhead)" },
                  "layout": { "type": "string", "enum": ["cover","section","content","two_col","stat","cards","process","text_image","table","quote"], "description": "Object placement for this slide. Pick by content: cover=title slide; section=divider; content=title+subtitle+bullets; two_col=comparison (provide columns); stat=2-4 big-number KPIs (provide metrics); cards=2-4 tiles/key points (provide cards); process=2-5 numbered steps (provide steps); text_image=figure (bullets left + image right); table=data (provide table); quote=one strong statement (in title). Vary layouts across the deck." },
                  "accent": { "type": "string", "description": "Override accent color hex (e.g. #2F5496). Usually omit — the template sets it." },
                  "bullets": { "type": "array", "items": { "type": "string" }, "description": "Single-column bullet lines" },
                  "columns": {
                    "type": "array",
                    "description": "Two-column layout (max 2); each column an optional heading + bullets. Overrides bullets.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "heading": { "type": "string" },
                        "bullets": { "type": "array", "items": { "type": "string" } }
                      }
                    }
                  },
                  "table": {
                    "type": "object",
                    "description": "A table placed below the text content",
                    "properties": {
                      "headers": { "type": "array", "items": { "type": "string" } },
                      "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } }
                    }
                  },
                  "metrics": {
                    "type": "array",
                    "description": "Big-number KPI callouts for layout 'stat' (2-4). Each: a short value (e.g. '73%', '5x', '2025') + a label under it.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "value": { "type": "string", "description": "The big number/figure" },
                        "label": { "type": "string", "description": "Short caption under the value" }
                      }
                    }
                  },
                  "cards": {
                    "type": "array",
                    "description": "Tile cards for layout 'cards' (2-4). Use instead of a bullet wall for key points. Each: a heading + one short body sentence.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "heading": { "type": "string" },
                        "body": { "type": "string" }
                      }
                    }
                  },
                  "steps": {
                    "type": "array",
                    "description": "Sequential steps for layout 'process' (2-5), rendered as a numbered horizontal flow. Each: a short label + optional caption.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "label": { "type": "string" },
                        "caption": { "type": "string" }
                      }
                    }
                  },
                  "shapes": {
                    "type": "array",
                    "description": "Free-form positioned shapes for diagrams (e.g. process boxes + arrows). Coordinates in INCHES; the slide is 13.33 x 7.5 (16:9). Drawn on top of the semantic layout.",
                    "items": {
                      "type": "object",
                      "properties": {
                        "type": { "type": "string", "enum": ["rect", "roundRect", "ellipse", "arrow", "chevron", "diamond"], "description": "Shape geometry (default rect)" },
                        "x": { "type": "number", "description": "Left, inches" },
                        "y": { "type": "number", "description": "Top, inches" },
                        "w": { "type": "number", "description": "Width, inches" },
                        "h": { "type": "number", "description": "Height, inches" },
                        "fill": { "type": "string", "description": "Fill color hex, e.g. #2F5496" },
                        "text": { "type": "string", "description": "Centered label (optional)" },
                        "fontColor": { "type": "string", "description": "Text color hex (e.g. #FFFFFF)" },
                        "fontSize": { "type": "integer", "description": "Text size in points (default 18)" },
                        "bold": { "type": "boolean" }
                      },
                      "required": ["x", "y", "w", "h"]
                    }
                  },
                  "image": {
                    "type": "object",
                    "description": "An image to place on the slide (e.g. a figure from ImageCreate). Coordinates in INCHES on the 13.33 x 7.5 (16:9) slide; drawn on top.",
                    "properties": {
                      "path": { "type": "string", "description": "Image path (.png/.jpg, relative to workspace)" },
                      "x": { "type": "number", "description": "Left, inches (default centers horizontally)" },
                      "y": { "type": "number", "description": "Top, inches (default 2.2)" },
                      "widthInches": { "type": "number", "description": "Width, inches (default 4)" }
                    },
                    "required": ["path"]
                  }
                }
              }
            }
          },
          "required": ["path", "slides"]
        }
        """).RootElement.Clone();

    private sealed record ColumnIn(
        [property: JsonPropertyName("heading")] string? Heading,
        [property: JsonPropertyName("bullets")] List<string>? Bullets);

    private sealed record TableIn(
        [property: JsonPropertyName("headers")] List<string>? Headers,
        [property: JsonPropertyName("rows")] List<List<string>>? Rows);

    private sealed record ShapeIn(
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

    private sealed record ImageIn(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("x")] double? X,
        [property: JsonPropertyName("y")] double? Y,
        [property: JsonPropertyName("widthInches")] double? WidthInches);

    private sealed record MetricIn(
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("label")] string? Label);

    private sealed record CardIn(
        [property: JsonPropertyName("heading")] string? Heading,
        [property: JsonPropertyName("body")] string? Body);

    private sealed record StepIn(
        [property: JsonPropertyName("label")] string? Label,
        [property: JsonPropertyName("caption")] string? Caption);

    private sealed record SlideIn(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("layout")] string? Layout,
        [property: JsonPropertyName("accent")] string? Accent,
        [property: JsonPropertyName("bullets")] List<string>? Bullets,
        [property: JsonPropertyName("columns")] List<ColumnIn>? Columns,
        [property: JsonPropertyName("table")] TableIn? Table,
        [property: JsonPropertyName("metrics")] List<MetricIn>? Metrics,
        [property: JsonPropertyName("cards")] List<CardIn>? Cards,
        [property: JsonPropertyName("steps")] List<StepIn>? Steps,
        [property: JsonPropertyName("shapes")] List<ShapeIn>? Shapes,
        [property: JsonPropertyName("image")] ImageIn? Image);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("template")] string? Template,
        [property: JsonPropertyName("slides")] List<SlideIn>? Slides);

    // 디자인 템플릿(프리셋) — 색·폰트·타이포 계층·정렬을 규격으로 고정한다.
    // "이쁘게 만들어" 같은 모호한 지시 대신, 검증된 템플릿 안에서 콘텐츠만 채운다.
    private sealed record ThemePreset(
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
        string FooterHex);   // 푸터(페이지번호·구분선) 무채색

    // A형: 연그레이 배경 + 좌측 accent 바 + 뚜렷한 타이포 계층(큰 제목/중간 부제/본문).
    private static readonly ThemePreset TemplateA = new(
        Name: "A", BgHex: "F7F8FA", AccentHex: "2F5496", TitleHex: "1F3864",
        SubtitleHex: "44546A", BodyHex: "333333",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 30, SubtitlePt: 17, BodyPt: 15,
        PanelHex: "FFFFFF", FooterHex: "AAB0BC");

    // B형: 흰 배경 + 큰 강조 타이포(키노트풍).
    private static readonly ThemePreset TemplateB = new(
        Name: "B", BgHex: "FFFFFF", AccentHex: "C00000", TitleHex: "C00000",
        SubtitleHex: "595959", BodyHex: "262626",
        TitleFont: "Arial", BodyFont: "Arial",
        TitlePt: 34, SubtitlePt: 18, BodyPt: 16,
        PanelHex: "F5F5F5", FooterHex: "B3B3B3");

    // C형: 미니멀(흰 배경·검정 타이포·회색 부제·얇은 무채색 바, 여백 큰).
    private static readonly ThemePreset TemplateC = new(
        Name: "C", BgHex: "FFFFFF", AccentHex: "222222", TitleHex: "111111",
        SubtitleHex: "888888", BodyHex: "333333",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 30, SubtitlePt: 16, BodyPt: 15,
        PanelHex: "F6F6F6", FooterHex: "BBBBBB");

    // D형: 다크(짙은 남색 배경·밝은 텍스트·시안 강조 포인트).
    private static readonly ThemePreset TemplateD = new(
        Name: "D", BgHex: "1F2430", AccentHex: "4FC3F7", TitleHex: "FFFFFF",
        SubtitleHex: "AEB6C7", BodyHex: "E3E8F0",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 32, SubtitlePt: 17, BodyPt: 15,
        PanelHex: "2A3242", FooterHex: "5A6478");

    private static ThemePreset ResolveTemplate(string? t) => t?.Trim().ToUpperInvariant() switch
    {
        "B" => TemplateB,
        "C" => TemplateC,
        "D" => TemplateD,
        _ => TemplateA, // 기본 A형
    };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path) || inp.Slides is null || inp.Slides.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.pptxCreate.inputRequired"), IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        var issues = new List<PptxLayoutCheck.Issue>();
        try
        {
            full = OpenXmlPaths.ResolveForWrite(context.WorkingDirectory, inp.Path, ".pptx");
            issues = Write(full, inp.Template, inp.Slides, context.WorkingDirectory);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        if (error is not null)
        {
            yield return new ToolOutput(L10n.Get("tools.pptxCreate.failed", error), IsError: true);
            yield break;
        }

        var msg = L10n.Get("tools.pptxCreate.ok", full, inp.Slides.Count);
        if (issues.Count > 0)
        {
            // 기하 QA 경고(겹침/화면밖) — 비-에러. 모델이 내용을 줄이거나 레이아웃을 바꾸도록 힌트.
            msg += "\n" + L10n.Get("tools.pptxCreate.layoutWarnFmt", issues.Count) + " "
                 + string.Join("; ", issues.Take(6).Select(i => $"[s{i.Slide}:{i.Kind}] {i.Detail}"));
        }

        yield return new ToolOutput(msg);
    }

    private static List<PptxLayoutCheck.Issue> Write(string path, string? template, List<SlideIn> slides, string workingDir)
    {
        var issues = new List<PptxLayoutCheck.Issue>();
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presPart = doc.AddPresentationPart();
        presPart.Presentation = new P.Presentation();

        // MoAI 고유 식별자를 심어 나중에 같은 대화로 되찾을 수 있게 한다(생성 문서 한정).
        doc.AddCustomFilePropertiesPart().Properties = OfficeDocId.Build(OfficeDocId.NewId());

        // slide master + layout (최소 1개 필요).
        var masterPart = presPart.AddNewPart<SlideMasterPart>();
        var layoutPart = masterPart.AddNewPart<SlideLayoutPart>();
        // 레이아웃은 마스터 색상맵을 상속(MasterColorMapping). OverrideColorMapping 은 12개 색상
        // 속성이 전부 필수라, 상속으로 두는 것이 단순하고 유효하다.
        layoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                NvGroupShapeProps(), new GroupShapeProperties())),
            new ColorMapOverride(new D.MasterColorMapping()));

        masterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                NvGroupShapeProps(), new GroupShapeProperties())),
            new P.ColorMap
            {
                Background1 = D.ColorSchemeIndexValues.Light1,
                Text1 = D.ColorSchemeIndexValues.Dark1,
                Background2 = D.ColorSchemeIndexValues.Light2,
                Text2 = D.ColorSchemeIndexValues.Dark2,
                Accent1 = D.ColorSchemeIndexValues.Accent1,
                Accent2 = D.ColorSchemeIndexValues.Accent2,
                Accent3 = D.ColorSchemeIndexValues.Accent3,
                Accent4 = D.ColorSchemeIndexValues.Accent4,
                Accent5 = D.ColorSchemeIndexValues.Accent5,
                Accent6 = D.ColorSchemeIndexValues.Accent6,
                Hyperlink = D.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = D.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList(new SlideLayoutId
            {
                Id = 2147483649U,
                RelationshipId = masterPart.GetIdOfPart(layoutPart),
            }));

        // 레이아웃 → 마스터 역관계(ECMA-376 필수). 없으면 검증은 통과해도 PowerPoint 가 '복구' 를 띄운다.
        layoutPart.AddPart(masterPart);

        // 테마(마스터에 필수).
        var themePart = masterPart.AddNewPart<ThemePart>();
        themePart.Theme = MinimalTheme();

        var preset = ResolveTemplate(template);
        var slideIdList = new SlideIdList();
        uint slideId = 256;
        var slideNo = 0;
        foreach (var s in slides)
        {
            slideNo++;
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.Slide = BuildSlide(s, preset, slideNo, slides.Count);
            slidePart.AddPart(layoutPart);

            if (s.Image is { Path: { } imgPath } && !string.IsNullOrWhiteSpace(imgPath))
            {
                var imgFull = OpenXmlPaths.ResolveForRead(workingDir, imgPath);
                if (!File.Exists(imgFull))
                {
                    throw new FileNotFoundException(L10n.Get("tools.pptxCreate.imageNotFound", imgPath));
                }

                var isTextImage = string.Equals(s.Layout?.Trim(), "text_image", StringComparison.OrdinalIgnoreCase);
                // text_image: 우측 절반에 맞춘 폭 기본값.
                var width = s.Image.WidthInches is > 0 ? s.Image.WidthInches!.Value : (isTextImage ? 4.2 : 4.0);
                var (cx, cy) = ImageEmbed.EmuSize(imgFull, width);
                var x = s.Image.X is { } xv ? (long)(xv * ImageEmbed.EmuPerInch)
                    : isTextImage ? SlideW - cx - MarginX          // 우측 정렬
                    : (SlideW - cx) / 2;                            // 중앙
                var y = s.Image.Y is { } yv ? (long)(yv * ImageEmbed.EmuPerInch)
                    : isTextImage ? BodyTop + 300000               // 본문 상단 맞춤
                    : (long)(2.2 * ImageEmbed.EmuPerInch);
                var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;
                tree.AppendChild(ImageEmbed.PptxPicture(slidePart, imgFull, x, y, cx, cy, 900U + slideId));
            }

            // 기하 QA — 텍스트 겹침/화면밖 검출(장식/배경 제외). 비-치명, 경고만 수집.
            issues.AddRange(PptxLayoutCheck.Inspect(slidePart.Slide, slideNo, SlideW, SlideH));

            slideIdList.AppendChild(new SlideId
            {
                Id = slideId++,
                RelationshipId = presPart.GetIdOfPart(slidePart),
            });
        }

        presPart.Presentation.Append(
            new SlideMasterIdList(new SlideMasterId
            {
                Id = 2147483648U,
                RelationshipId = presPart.GetIdOfPart(masterPart),
            }),
            slideIdList,
            new SlideSize { Cx = (int)SlideW, Cy = (int)SlideH },
            new NotesSize { Cx = 6858000, Cy = 9144000 });

        return issues;
    }

    // 슬라이드 기하(EMU). 12192000×6858000 = 16:9(와이드). 세로(H)는 4:3과 동일하므로
    // 세로 배치 상수는 그대로 두고 가로만 넓어진다 — 기존 레이아웃 수직 흐름 무영향.
    private const long SlideW = 12192000;     // 16:9 슬라이드 폭
    private const long SlideH = 6858000;      // 슬라이드 높이
    private const long LeftBarW = 110000;     // 좌측 accent 세로 바 폭
    private const long MarginX = 685800;      // 0.75"
    private const long ContentW = 10820400;   // 슬라이드 폭 - 좌우 여백(16:9)
    private const long BodyTop = 1500000;
    private const long BodyBottom = 6500000;
    private const long RowHeight = 370840;    // 표 행 높이 ≈ 0.4"
    private const long MinRowHeight = 210000; // 최소 행 높이 ≈ 0.23"(auto-fit 하한)
    private const string DefaultAccent = "2F5496";

    // 불릿 개수가 많을수록 시작 폰트를 줄여 슬라이드 밖으로 넘치는 것을 완화(normAutofit 과 병행).
    private static int BulletFontSize(int count) => count switch
    {
        <= 5 => 1800,
        <= 8 => 1600,
        <= 11 => 1400,
        <= 15 => 1200,
        _ => 1050,
    };

    // 템플릿(디자인 프리셋) × 레이아웃(객체 배치)로 슬라이드를 만든다.
    // 디자인(색·타이포·정렬)은 프리셋이 규격으로 고정하고, 콘텐츠만 채운다.
    private static Slide BuildSlide(SlideIn s, ThemePreset p, int index, int total)
    {
        var tree = new ShapeTree(NvGroupShapeProps(), new GroupShapeProperties());
        uint id = 2;

        // 공통 디자인: 배경 + 좌측 accent 세로 바(AccentBar 는 채운 사각형이라 배경에도 재사용).
        tree.AppendChild(AccentBar(id++, 0, 0, SlideW, SlideH, p.BgHex));
        tree.AppendChild(AccentBar(id++, 0, 0, LeftBarW, SlideH, p.AccentHex));

        var layout = (s.Layout ?? "content").Trim().ToLowerInvariant();

        // ── cover: 중앙 큰 제목 + 부제 ──
        if (layout == "cover")
        {
            tree.AppendChild(MakeShape(id++, "Title", MarginX, 2600000, ContentW, 1200000,
                new[] { CenteredText(s.Title ?? string.Empty, p.TitlePt + 12, true, p.TitleHex, p.TitleFont) }));
            tree.AppendChild(AccentBar(id++, (SlideW - 1400000) / 2, 3860000, 1400000, 44000, p.AccentHex));
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
            {
                tree.AppendChild(MakeShape(id++, "Subtitle", MarginX, 4000000, ContentW, 700000,
                    new[] { CenteredText(s.Subtitle!, p.SubtitlePt, false, p.SubtitleHex, p.BodyFont) }));
            }

            return WrapSlide(tree);
        }

        // ── section: 구간 구분(좌측 강조 블록 + 큰 제목 + 부제) ──
        if (layout == "section")
        {
            tree.AppendChild(AccentBar(id++, MarginX, 2550000, 300000, 1000000, p.AccentHex));
            tree.AppendChild(MakeShape(id++, "Title", MarginX + 480000, 2660000, ContentW - 480000, 880000,
                new[] { TextParagraph(s.Title ?? string.Empty, (p.TitlePt + 6) * 100, bold: true, bullet: false, color: p.TitleHex, fontName: p.TitleFont) }));
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
            {
                tree.AppendChild(MakeShape(id++, "Subtitle", MarginX + 480000, 3560000, ContentW - 480000, 480000,
                    new[] { TextParagraph(s.Subtitle!, p.SubtitlePt * 100, bold: false, bullet: false, color: p.SubtitleHex, fontName: p.BodyFont) }));
            }

            return WrapSlide(tree);
        }

        // ── quote: 큰 문구 하나(중앙) ──
        if (layout == "quote")
        {
            tree.AppendChild(MakeShape(id++, "Quote", MarginX + 300000, 2300000, ContentW - 600000, 2200000,
                new[] { CenteredText(s.Title ?? s.Subtitle ?? string.Empty, p.SubtitlePt + 12, true, p.TitleHex, p.TitleFont) }));
            tree.AppendChild(AccentBar(id++, (SlideW - 1400000) / 2, 4650000, 1400000, 44000, p.AccentHex));
            return WrapSlide(tree);
        }

        // ── content / text_image 공통: 좌측 정렬 제목 + 부제 + 짧은 강조바 ──
        long bodyTop = BodyTop;
        if (!string.IsNullOrWhiteSpace(s.Title))
        {
            tree.AppendChild(MakeShape(id++, "Title", MarginX, 360000, ContentW, 720000,
                new[] { TextParagraph(s.Title!, p.TitlePt * 100, bold: true, bullet: false, color: p.TitleHex, fontName: p.TitleFont) }));
            long y = 1080000;
            if (!string.IsNullOrWhiteSpace(s.Subtitle))
            {
                tree.AppendChild(MakeShape(id++, "Subtitle", MarginX, y, ContentW, 440000,
                    new[] { TextParagraph(s.Subtitle!, p.SubtitlePt * 100, bold: false, bullet: false, color: p.SubtitleHex, fontName: p.BodyFont) }));
                y += 470000;
            }

            tree.AppendChild(AccentBar(id++, MarginX, y + 30000, 820000, 42000, p.AccentHex));
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
                // 배경 카드 + 상단 accent 칩 — '떠 있는 텍스트'가 아니라 구조를 가진 카드로 읽히게.
                tree.AppendChild(Panel(id++, x, bodyTop, colW, cardH, p.PanelHex));
                tree.AppendChild(AccentBar(id++, x + pad, bodyTop + pad, 300000, 46000, p.AccentHex));

                var bullets = cols[i].Bullets ?? new List<string>();
                var colSize = Math.Min(p.BodyPt * 100, BulletFontSize(bullets.Count));
                var colGap = BulletSpaceBefore(bullets.Count);
                var paras = new List<D.Paragraph>();
                if (!string.IsNullOrWhiteSpace(cols[i].Heading))
                {
                    paras.Add(TextParagraph(cols[i].Heading!, p.SubtitlePt * 100, bold: true, bullet: false, color: p.AccentHex, fontName: p.BodyFont));
                }

                foreach (var b in bullets)
                {
                    paras.Add(TextParagraph(b, colSize, bold: false, bullet: true, color: p.BodyHex, fontName: p.BodyFont, spaceBeforePct: colGap));
                }

                if (paras.Count == 0)
                {
                    paras.Add(TextParagraph(string.Empty, p.BodyPt * 100, false, false, null));
                }

                // 텍스트는 칩 아래로 인셋 배치(카드 안쪽 패딩 반영).
                tree.AppendChild(MakeShape(id++, $"Col{i + 1}", x + pad, bodyTop + pad + 120000, colW - 2 * pad, cardH - 2 * pad - 120000, paras));
            }
        }
        else if (hasBullets)
        {
            var size = Math.Min(p.BodyPt * 100, BulletFontSize(s.Bullets!.Count));
            var gap = BulletSpaceBefore(s.Bullets!.Count);
            var paras = s.Bullets!.Select(b => TextParagraph(b, size, bold: false, bullet: true, color: p.BodyHex, fontName: p.BodyFont, spaceBeforePct: gap));

            // text_image 는 우측 이미지와 균형을 위해 카드 없이. 그 외 단일 본문은 카드로 프레이밍
            // (덱 전체를 카드 언어로 통일 — 빈 하단이 '카드 패딩'으로 읽혀 데드스페이스가 정돈된다).
            if (layout == "text_image")
            {
                tree.AppendChild(MakeShape(id++, "Body", MarginX, bodyTop, bodyW, bodyH, paras));
            }
            else
            {
                const long pad = 320000;      // 카드 안쪽 여백 ≈ 0.35"
                tree.AppendChild(Panel(id++, MarginX, bodyTop, ContentW, bodyH, p.PanelHex));
                tree.AppendChild(AccentBar(id++, MarginX + pad, bodyTop + pad, 300000, 46000, p.AccentHex));
                tree.AppendChild(MakeShape(id++, "Body", MarginX + pad, bodyTop + pad + 130000, ContentW - 2 * pad, bodyH - 2 * pad - 130000, paras));
            }
        }
        else if (hasMetrics)
        {
            BuildStatRow(tree, ref id, bodyTop, bodyH, s.Metrics!, p);
        }
        else if (hasCards)
        {
            BuildCardGrid(tree, ref id, bodyTop, bodyH, s.Cards!, p);
        }
        else if (hasSteps)
        {
            BuildProcess(tree, ref id, bodyTop, bodyH, s.Steps!, p);
        }

        if (hasTable)
        {
            tree.AppendChild(BuildTable(id++, MarginX, bodyTop, ContentW, s.Table!, p.AccentHex, BodyBottom - bodyTop));
        }

        if (s.Shapes is { Count: > 0 })
        {
            foreach (var sh in s.Shapes!)
            {
                tree.AppendChild(CustomShape(id++, sh));
            }
        }

        // 콘텐츠 계열 슬라이드 하단 푸터(구분선 + 페이지 번호).
        AppendFooter(tree, ref id, index, total, p);
        return WrapSlide(tree);
    }

    private static Slide WrapSlide(ShapeTree tree) =>
        new(new CommonSlideData(tree), new ColorMapOverride(new D.MasterColorMapping()));

    private const long EmuPerInch = 914400;

    // 자유 도형(다이어그램용). 좌표는 인치 → EMU. 선택적 채우기·중앙정렬 텍스트.
    private static P.Shape CustomShape(uint id, ShapeIn sh)
    {
        var x = (long)((sh.X ?? 0) * EmuPerInch);
        var y = (long)((sh.Y ?? 0) * EmuPerInch);
        var w = (long)((sh.W ?? 1) * EmuPerInch);
        var h = (long)((sh.H ?? 1) * EmuPerInch);

        var spPr = new P.ShapeProperties(
            new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = h }),
            new D.PresetGeometry(new D.AdjustValueList()) { Preset = MapGeometry(sh.Type) });
        var fill = ParseColor(sh.Fill);
        if (fill is not null)
        {
            spPr.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = fill }));
        }

        var body = new P.TextBody(
            new D.BodyProperties { Anchor = D.TextAnchoringTypeValues.Center },
            new D.ListStyle(),
            CenteredText(sh.Text ?? string.Empty, sh.FontSize ?? 18, sh.Bold == true, ParseColor(sh.FontColor)));

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Shape" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            spPr,
            body);
    }

    // 정렬 지정 가능한 단일 문단(푸터·라벨 등). CenteredText 의 일반화.
    private static D.Paragraph AlignedText(string text, int fontSizePt, bool bold, string? color, string? fontName, D.TextAlignmentTypeValues align)
    {
        var runProps = new D.RunProperties { Language = "en-US", FontSize = fontSizePt * 100 };
        if (bold)
        {
            runProps.Bold = true;
        }

        if (color is not null)
        {
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        }

        if (!string.IsNullOrEmpty(fontName))
        {
            runProps.AppendChild(new D.LatinFont { Typeface = fontName });
        }

        // 한글(East Asian): 런이 명시적 Latin 폰트를 가지면 테마 EA 폰트로 폴백되지 않으므로 런에 직접 지정.
        runProps.AppendChild(new D.EastAsianFont { Typeface = "Malgun Gothic" });

        var para = new D.Paragraph(new D.ParagraphProperties(new D.NoBullet()) { Alignment = align });
        para.AppendChild(new D.Run(runProps, new D.Text(text)));
        return para;
    }

    private static D.Paragraph CenteredText(string text, int fontSizePt, bool bold, string? color, string? fontName = null)
    {
        var runProps = new D.RunProperties { Language = "en-US", FontSize = fontSizePt * 100 };
        if (bold)
        {
            runProps.Bold = true;
        }

        if (color is not null)
        {
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        }

        if (!string.IsNullOrEmpty(fontName))
        {
            runProps.AppendChild(new D.LatinFont { Typeface = fontName });
        }

        // 한글(East Asian): 런이 명시적 Latin 폰트를 가지면 테마 EA 폰트로 폴백되지 않으므로 런에 직접 지정.
        runProps.AppendChild(new D.EastAsianFont { Typeface = "Malgun Gothic" });

        var para = new D.Paragraph(new D.ParagraphProperties(new D.NoBullet()) { Alignment = D.TextAlignmentTypeValues.Center });
        para.AppendChild(new D.Run(runProps, new D.Text(text)));
        return para;
    }

    private static D.ShapeTypeValues MapGeometry(string? type) => (type?.Trim().ToLowerInvariant()) switch
    {
        "roundrect" => D.ShapeTypeValues.RoundRectangle,
        "ellipse" or "circle" => D.ShapeTypeValues.Ellipse,
        "arrow" or "rightarrow" => D.ShapeTypeValues.RightArrow,
        "chevron" => D.ShapeTypeValues.Chevron,
        "diamond" => D.ShapeTypeValues.Diamond,
        _ => D.ShapeTypeValues.Rectangle,
    };

    // 위치·크기(xfrm)와 사각형 지오메트리를 갖춘 텍스트 도형. spPr 이 비면 PowerPoint 가 렌더하지 못한다.
    // anchor: 상자 안 수직 정렬. 본문 불릿을 Center 로 두면 콘텐츠가 적을 때 상단에 몰려 하단이
    // 텅 비는 '데드스페이스'를 없애고 시각적으로 균형이 잡힌다(제목·부제는 상단 유지=null).
    private static P.Shape MakeShape(
        uint id, string name, long x, long y, long cx, long cy, IEnumerable<D.Paragraph> paragraphs,
        D.TextAnchoringTypeValues? anchor = null)
    {
        // normAutofit: 텍스트가 상자를 넘치면 PowerPoint 가 폰트를 자동 축소(오버플로우 방지).
        var bodyProps = new D.BodyProperties(new D.NormalAutoFit());
        if (anchor is { } a)
        {
            bodyProps.Anchor = a;
        }

        var body = new P.TextBody(bodyProps, new D.ListStyle());
        foreach (var p in paragraphs)
        {
            body.AppendChild(p);
        }

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new D.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(
                    new D.Offset { X = x, Y = y },
                    new D.Extents { Cx = cx, Cy = cy }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle }),
            body);
    }

    // 색상으로 채운 얇은 사각형(제목 밑줄 강조바 등).
    private static P.Shape AccentBar(uint id, long x, long y, long w, long h, string color)
    {
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "AccentBar" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = h }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Rectangle },
                new D.SolidFill(new D.RgbColorModelHex { Val = color })),
            new P.TextBody(new D.BodyProperties(), new D.ListStyle(), new D.Paragraph()));
    }

    // 카드/패널: 살짝 둥근 모서리의 채운 사각형(본문을 담는 배경 카드). 텍스트보다 먼저 그려 뒤에 깔린다.
    private static P.Shape Panel(uint id, long x, long y, long w, long h, string color)
    {
        var geo = new D.PresetGeometry(
            new D.AdjustValueList(new D.ShapeGuide { Name = "adj", Formula = "val 4200" }))
        { Preset = D.ShapeTypeValues.RoundRectangle };
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Panel" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = h }),
                geo,
                new D.SolidFill(new D.RgbColorModelHex { Val = color })),
            new P.TextBody(new D.BodyProperties(), new D.ListStyle(), new D.Paragraph()));
    }

    // 불릿 수가 적을수록 문단 앞 여백(spaceBefore, %)을 키워 세로로 고르게 퍼뜨린다.
    // (상단 앵커 유지 + 하단 데드스페이스 완화. 넘치면 normAutofit 이 폰트를 줄여 보호.)
    private static int BulletSpaceBefore(int count) => count switch
    {
        <= 3 => 160000,  // 160%
        <= 4 => 120000,  // 120%
        <= 5 => 80000,   // 80%
        <= 6 => 45000,   // 45%
        _ => 20000,      // 20%
    };

    // 하단 푸터: 얇은 구분선 + "n / N" 페이지 번호(무채색). 콘텐츠 슬라이드의 바닥을 정돈해 준다.
    private static void AppendFooter(ShapeTree tree, ref uint id, int index, int total, ThemePreset p)
    {
        const long footY = 6480000;
        tree.AppendChild(AccentBar(id++, MarginX, footY, 460000, 26000, p.AccentHex));
        tree.AppendChild(MakeShape(id++, "PageNo", SlideW - MarginX - 900000, footY - 120000, 900000, 300000,
            new[] { AlignedText($"{index} / {total}", 11, false, p.FooterHex, p.BodyFont, D.TextAlignmentTypeValues.Right) }));
    }

    // 채운 원 + 중앙 번호(프로세스 단계 배지 등).
    private static P.Shape Circle(uint id, long x, long y, long d, string fill, string text, int fontPt, string fontColor)
    {
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Badge" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = d, Cy = d }),
                new D.PresetGeometry(new D.AdjustValueList()) { Preset = D.ShapeTypeValues.Ellipse },
                new D.SolidFill(new D.RgbColorModelHex { Val = fill })),
            new P.TextBody(
                new D.BodyProperties { Anchor = D.TextAnchoringTypeValues.Center },
                new D.ListStyle(),
                CenteredText(text, fontPt, true, fontColor)));
    }

    // ── stat: 2~4개의 큰 수치 콜아웃(KPI). 배경 카드 + 큰 값(accent) + 라벨. 세로 중앙 정렬. ──
    private static void BuildStatRow(ShapeTree tree, ref uint id, long bodyTop, long bodyH, List<MetricIn> metrics, ThemePreset p)
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
            tree.AppendChild(Panel(id++, x, cy, cardW, cardH, p.PanelHex));
            tree.AppendChild(MakeShape(id++, "Stat", x, cy + 320000, cardW, 900000,
                new[] { CenteredText(items[i].Value ?? string.Empty, 44, true, p.AccentHex, p.TitleFont) }));
            tree.AppendChild(MakeShape(id++, "StatLabel", x + 180000, cy + 1300000, cardW - 360000, 640000,
                new[] { CenteredText(items[i].Label ?? string.Empty, p.SubtitlePt, false, p.SubtitleHex, p.BodyFont) }));
        }
    }

    // ── cards: 2~4개의 타일(요점 카드). 4개는 2×2 그리드. 각 카드 = 패널 + accent 칩 + 헤딩 + 본문. ──
    private static void BuildCardGrid(ShapeTree tree, ref uint id, long bodyTop, long bodyH, List<CardIn> cards, ThemePreset p)
    {
        var items = cards.Take(4).ToList();
        var n = items.Count;
        var cols = n <= 3 ? n : 2;
        var rows = (n + cols - 1) / cols;
        const long gap = 300000;
        const long pad = 240000;
        var cardW = (ContentW - gap * (cols - 1)) / cols;
        var cardH = (bodyH - gap * (rows - 1)) / rows;
        for (var i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            var x = MarginX + c * (cardW + gap);
            var y = bodyTop + r * (cardH + gap);
            tree.AppendChild(Panel(id++, x, y, cardW, cardH, p.PanelHex));
            tree.AppendChild(AccentBar(id++, x + pad, y + pad, 300000, 46000, p.AccentHex));
            var paras = new List<D.Paragraph>();
            if (!string.IsNullOrWhiteSpace(items[i].Heading))
            {
                paras.Add(TextParagraph(items[i].Heading!, p.SubtitlePt * 100, bold: true, bullet: false, color: p.AccentHex, fontName: p.BodyFont));
            }

            if (!string.IsNullOrWhiteSpace(items[i].Body))
            {
                paras.Add(TextParagraph(items[i].Body!, p.BodyPt * 100, bold: false, bullet: false, color: p.BodyHex, fontName: p.BodyFont, spaceBeforePct: 45000));
            }

            if (paras.Count == 0)
            {
                paras.Add(TextParagraph(string.Empty, p.BodyPt * 100, false, false, null));
            }

            tree.AppendChild(MakeShape(id++, "Card", x + pad, y + pad + 120000, cardW - 2 * pad, cardH - 2 * pad - 120000, paras));
        }
    }

    // ── process: 2~5개 단계의 가로 흐름. 각 단계 = 패널 + 번호 배지(원) + 라벨 + 캡션, 사이에 › 화살표. ──
    private static void BuildProcess(ShapeTree tree, ref uint id, long bodyTop, long bodyH, List<StepIn> steps, ThemePreset p)
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
            tree.AppendChild(Panel(id++, x, cy, stepW, stepH, p.PanelHex));
            tree.AppendChild(Circle(id++, x + (stepW - badge) / 2, cy + 230000, badge, p.AccentHex, (i + 1).ToString(), 22, "FFFFFF"));
            tree.AppendChild(MakeShape(id++, "StepLabel", x + 120000, cy + 230000 + badge + 70000, stepW - 240000, 520000,
                new[] { CenteredText(items[i].Label ?? string.Empty, p.SubtitlePt, true, p.TitleHex, p.BodyFont) }));
            if (!string.IsNullOrWhiteSpace(items[i].Caption))
            {
                tree.AppendChild(MakeShape(id++, "StepCap", x + 120000, cy + 230000 + badge + 640000, stepW - 240000, 760000,
                    new[] { CenteredText(items[i].Caption!, Math.Max(10, p.BodyPt - 1), false, p.BodyHex, p.BodyFont) }));
            }

            if (i < n - 1)
            {
                tree.AppendChild(MakeShape(id++, "Arrow", x + stepW - 40000, cy + (stepH / 2) - 220000, gap + 80000, 440000,
                    new[] { CenteredText("›", 28, true, p.AccentHex, p.BodyFont) }));
            }
        }
    }

    private static D.Paragraph TextParagraph(string text, int fontSize, bool bold, bool bullet, string? color, string? fontName = null, int spaceBeforePct = 0)
    {
        var runProps = new D.RunProperties { Language = "en-US", FontSize = fontSize };
        if (bold)
        {
            runProps.Bold = true;
        }

        // RunProperties 자식 순서(스키마): fill(SolidFill) → latin(LatinFont).
        if (color is not null)
        {
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        }

        if (!string.IsNullOrEmpty(fontName))
        {
            runProps.AppendChild(new D.LatinFont { Typeface = fontName });
        }

        // 한글(East Asian): 런이 명시적 Latin 폰트를 가지면 테마 EA 폰트로 폴백되지 않으므로 런에 직접 지정.
        runProps.AppendChild(new D.EastAsianFont { Typeface = "Malgun Gothic" });

        var para = new D.Paragraph();
        // 본문 불릿: 줄간격 여유(120%)로 매달린 줄·과밀 완화. 불릿이 적을수록 spaceBefore(문단 앞 여백)를
        // 키워 세로로 고르게 퍼뜨린다(상단 앵커 유지 + 하단 데드스페이스 완화). 불릿 색은 본문 색에 맞춘다
        // (다크 배경에서 검정 불릿이 안 보이는 것 방지). 자식 순서: lnSpc → spcBef → buClr → buFont → buChar.
        D.ParagraphProperties pPr;
        if (bullet)
        {
            pPr = new D.ParagraphProperties(new D.LineSpacing(new D.SpacingPercent { Val = 120000 }));
            if (spaceBeforePct > 0)
            {
                pPr.AppendChild(new D.SpaceBefore(new D.SpacingPercent { Val = spaceBeforePct }));
            }

            if (color is not null)
            {
                pPr.AppendChild(new D.BulletColor(new D.RgbColorModelHex { Val = color }));
            }

            pPr.AppendChild(new D.BulletFont { Typeface = "Arial" });
            pPr.AppendChild(new D.CharacterBullet { Char = "•" });
        }
        else
        {
            pPr = new D.ParagraphProperties();
            if (spaceBeforePct > 0)
            {
                pPr.AppendChild(new D.SpaceBefore(new D.SpacingPercent { Val = spaceBeforePct }));
            }

            pPr.AppendChild(new D.NoBullet());
        }

        para.AppendChild(pPr);
        para.AppendChild(new D.Run(runProps, new D.Text(text)));
        return para;
    }

    // 표(graphicFrame + a:tbl). 헤더 행은 accent 배경 + 흰 볼드.
    // availHeight: 표에 허용된 세로 공간. 행이 많으면 행 높이·폰트를 줄여 슬라이드 밖으로 넘치지 않게 한다.
    private static P.GraphicFrame BuildTable(uint id, long x, long y, long w, TableIn t, string accent, long availHeight)
    {
        var headers = t.Headers ?? new List<string>();
        var rows = t.Rows ?? new List<List<string>>();
        var ncols = Math.Max(headers.Count, rows.Count > 0 ? rows.Max(r => r.Count) : 0);
        if (ncols == 0)
        {
            ncols = 1;
        }

        var colW = w / ncols;

        // 행 높이 적응: 가용 높이/행수. 기본보다 크게는 안 늘리고, 하한 아래로는 안 줄인다.
        var nrows = (headers.Count > 0 ? 1 : 0) + rows.Count;
        var rowH = nrows > 0
            ? Math.Max(MinRowHeight, Math.Min(RowHeight, availHeight / nrows))
            : RowHeight;
        // 행 높이가 기본보다 작아지면 폰트도 같은 비율로 축소(하한 있음).
        var fontScale = (double)rowH / RowHeight;

        var table = new D.Table(new D.TableProperties { FirstRow = true });
        var grid = new D.TableGrid();
        for (var c = 0; c < ncols; c++)
        {
            grid.AppendChild(new D.GridColumn { Width = colW });
        }

        table.AppendChild(grid);

        if (headers.Count > 0)
        {
            table.AppendChild(BuildRow(headers, ncols, header: true, accent, rowH, fontScale));
        }

        foreach (var r in rows)
        {
            table.AppendChild(BuildRow(r, ncols, header: false, accent, rowH, fontScale));
        }

        var totalH = rowH * nrows;
        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Table" },
                new P.NonVisualGraphicFrameDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = totalH }),
            new D.Graphic(new D.GraphicData(table)
            {
                Uri = "http://schemas.openxmlformats.org/drawingml/2006/table",
            }));
    }

    private static D.TableRow BuildRow(List<string> cells, int ncols, bool header, string accent, long rowH, double fontScale)
    {
        var tr = new D.TableRow { Height = rowH };
        for (var c = 0; c < ncols; c++)
        {
            tr.AppendChild(BuildCell(c < cells.Count ? cells[c] : string.Empty, header, accent, fontScale));
        }

        return tr;
    }

    private static D.TableCell BuildCell(string text, bool header, string accent, double fontScale)
    {
        var baseSize = header ? 1600 : 1400;
        var size = Math.Max(900, (int)(baseSize * fontScale)); // 축소 시 하한 9pt
        var runProps = new D.RunProperties { Language = "en-US", FontSize = size };
        if (header)
        {
            runProps.Bold = true;
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = "FFFFFF" }));
        }

        var body = new D.TextBody(
            new D.BodyProperties(),
            new D.ListStyle(),
            new D.Paragraph(new D.ParagraphProperties(new D.NoBullet()), new D.Run(runProps, new D.Text(text))));

        var cellProps = new D.TableCellProperties();
        if (header)
        {
            cellProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = accent }));
        }

        return new D.TableCell(body, cellProps);
    }

    private static string? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        var h = hex.TrimStart('#').Trim();
        return h.Length == 6 && h.All(Uri.IsHexDigit) ? h.ToUpperInvariant() : null;
    }

    private static P.NonVisualGroupShapeProperties NvGroupShapeProps() =>
        new(
            new P.NonVisualDrawingProperties { Id = 1U, Name = string.Empty },
            new P.NonVisualGroupShapeDrawingProperties(),
            new P.ApplicationNonVisualDrawingProperties());

    // 유효 pptx 에 필요한 최소 테마(색/폰트/포맷 스킴).
    private static D.Theme MinimalTheme()
    {
        var scheme = new D.ColorScheme(
            new D.Dark1Color(new D.SystemColor { Val = D.SystemColorValues.WindowText }),
            new D.Light1Color(new D.SystemColor { Val = D.SystemColorValues.Window }),
            new D.Dark2Color(new D.RgbColorModelHex { Val = "44546A" }),
            new D.Light2Color(new D.RgbColorModelHex { Val = "E7E6E6" }),
            new D.Accent1Color(new D.RgbColorModelHex { Val = "4472C4" }),
            new D.Accent2Color(new D.RgbColorModelHex { Val = "ED7D31" }),
            new D.Accent3Color(new D.RgbColorModelHex { Val = "A5A5A5" }),
            new D.Accent4Color(new D.RgbColorModelHex { Val = "FFC000" }),
            new D.Accent5Color(new D.RgbColorModelHex { Val = "5B9BD5" }),
            new D.Accent6Color(new D.RgbColorModelHex { Val = "70AD47" }),
            new D.Hyperlink(new D.RgbColorModelHex { Val = "0563C1" }),
            new D.FollowedHyperlinkColor(new D.RgbColorModelHex { Val = "954F72" }))
        { Name = "Office" };

        // EA(동아시아) 폰트를 명시 — 한글 텍스트가 Windows/PowerPoint 에서 일관되게 렌더되도록.
        // Latin 은 템플릿별 런에서 지정하고, 한글 글리프는 이 EA 폰트를 따른다.
        var fontScheme = new D.FontScheme(
            new D.MajorFont(new D.LatinFont { Typeface = "Calibri Light" }, new D.EastAsianFont { Typeface = "Malgun Gothic" }, new D.ComplexScriptFont { Typeface = string.Empty }),
            new D.MinorFont(new D.LatinFont { Typeface = "Calibri" }, new D.EastAsianFont { Typeface = "Malgun Gothic" }, new D.ComplexScriptFont { Typeface = string.Empty }))
        { Name = "Office" };

        var fmtScheme = new D.FormatScheme(
            new D.FillStyleList(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })),
            new D.LineStyleList(new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })), new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })), new D.Outline(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }))),
            new D.EffectStyleList(new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList()), new D.EffectStyle(new D.EffectList())),
            new D.BackgroundFillStyleList(new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor }), new D.SolidFill(new D.SchemeColor { Val = D.SchemeColorValues.PhColor })))
        { Name = "Office" };

        return new D.Theme(
            new D.ThemeElements(scheme, fontScheme, fmtScheme),
            new D.ObjectDefaults())
        { Name = "Office Theme" };
    }
}
