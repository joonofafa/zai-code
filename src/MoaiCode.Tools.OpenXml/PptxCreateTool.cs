using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using D = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 새 PowerPoint 프레젠테이션(.pptx)을 만든다. Open XML SDK — PowerPoint 설치 불필요, 전 플랫폼.
/// 최소 유효 구조(presentation + slide master + slide layout + slides)를 직접 조립한다.
/// </summary>
public sealed class PptxCreateTool : ITool
{
    public string Name => "PptxCreate";

    public string Description => """
        Creates a new PowerPoint presentation (.pptx) using the built-in Open XML writer — no
        dependencies, no PowerPoint install. DESIGN IS TEMPLATE-DRIVEN: pick a "template" (A/B) and a
        per-slide "layout"; the template fixes colors, fonts, typography hierarchy (title/subtitle/body),
        spacing and alignment, so you ONLY supply content. Do NOT try to hand-tune a "pretty" design —
        choose the right layout and write concise content instead.
        Per slide provide: title, subtitle, and content for the chosen layout —
          - cover: title + subtitle (centered) — the opening slide
          - section: a divider between parts (title + subtitle)
          - content: title + subtitle + bullets (default). Keep bullets to <=5 lines, each ONE short line.
          - two_col: comparison — provide "columns" (max 2; each heading + bullets)
          - text_image: explanation with a figure — bullets on the left + "image" on the right
          - table: data — provide "table" (headers + rows)
          - quote: one strong statement — put it in "title"
        "shapes" places free-form boxes/arrows (rect/roundRect/ellipse/arrow/chevron/diamond) at inch
        coordinates (slide is 10 x 7.5) for diagrams. ALWAYS use this to produce a .pptx file. Do NOT
        install packages (pptxgenjs, python-pptx, etc.) or write scripts. For editing an OPEN presentation
        on Windows, use PowerPointEdit.
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
                  "layout": { "type": "string", "enum": ["cover","section","content","two_col","text_image","table","quote"], "description": "Object placement for this slide. Pick by content: cover=title slide (title+subtitle centered); section=divider between parts; content=title+subtitle+bullets (default); two_col=comparison (provide columns); text_image=explanation with a figure (bullets left + image right, provide image); table=data (provide table); quote=one strong statement (put it in title)." },
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
                  "shapes": {
                    "type": "array",
                    "description": "Free-form positioned shapes for diagrams (e.g. process boxes + arrows). Coordinates in INCHES; the slide is 10 x 7.5. Drawn on top of the semantic layout.",
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
                    "description": "An image to place on the slide (e.g. a figure from ImageCreate). Coordinates in INCHES on the 10 x 7.5 slide; drawn on top.",
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

    private sealed record SlideIn(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("subtitle")] string? Subtitle,
        [property: JsonPropertyName("layout")] string? Layout,
        [property: JsonPropertyName("accent")] string? Accent,
        [property: JsonPropertyName("bullets")] List<string>? Bullets,
        [property: JsonPropertyName("columns")] List<ColumnIn>? Columns,
        [property: JsonPropertyName("table")] TableIn? Table,
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
        int BodyPt);         // 본문 pt

    // A형: 연그레이 배경 + 좌측 accent 바 + 뚜렷한 타이포 계층(큰 제목/중간 부제/본문).
    private static readonly ThemePreset TemplateA = new(
        Name: "A", BgHex: "F7F8FA", AccentHex: "2F5496", TitleHex: "1F3864",
        SubtitleHex: "44546A", BodyHex: "333333",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 30, SubtitlePt: 17, BodyPt: 15);

    // B형: 흰 배경 + 큰 강조 타이포(키노트풍).
    private static readonly ThemePreset TemplateB = new(
        Name: "B", BgHex: "FFFFFF", AccentHex: "C00000", TitleHex: "C00000",
        SubtitleHex: "595959", BodyHex: "262626",
        TitleFont: "Arial", BodyFont: "Arial",
        TitlePt: 34, SubtitlePt: 18, BodyPt: 16);

    // C형: 미니멀(흰 배경·검정 타이포·회색 부제·얇은 무채색 바, 여백 큰).
    private static readonly ThemePreset TemplateC = new(
        Name: "C", BgHex: "FFFFFF", AccentHex: "222222", TitleHex: "111111",
        SubtitleHex: "888888", BodyHex: "333333",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 30, SubtitlePt: 16, BodyPt: 15);

    // D형: 다크(짙은 남색 배경·밝은 텍스트·시안 강조 포인트).
    private static readonly ThemePreset TemplateD = new(
        Name: "D", BgHex: "1F2430", AccentHex: "4FC3F7", TitleHex: "FFFFFF",
        SubtitleHex: "AEB6C7", BodyHex: "E3E8F0",
        TitleFont: "Calibri Light", BodyFont: "Calibri",
        TitlePt: 32, SubtitlePt: 17, BodyPt: 15);

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
            yield return new ToolOutput("PptxCreate: 'path' 와 최소 1개 'slides' 가 필요합니다.", IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        try
        {
            full = OpenXmlPaths.ResolveForWrite(context.WorkingDirectory, inp.Path, ".pptx");
            Write(full, inp.Template, inp.Slides, context.WorkingDirectory);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        yield return error is not null
            ? new ToolOutput($"PptxCreate: 실패 — {error}", IsError: true)
            : new ToolOutput($"OK: {full} 생성 ({inp.Slides.Count} 슬라이드).");
    }

    private static void Write(string path, string? template, List<SlideIn> slides, string workingDir)
    {
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
        foreach (var s in slides)
        {
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.Slide = BuildSlide(s, preset);
            slidePart.AddPart(layoutPart);

            if (s.Image is { Path: { } imgPath } && !string.IsNullOrWhiteSpace(imgPath))
            {
                var imgFull = OpenXmlPaths.ResolveForRead(workingDir, imgPath);
                if (!File.Exists(imgFull))
                {
                    throw new FileNotFoundException($"이미지 없음: {imgPath}");
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
            new SlideSize { Cx = 9144000, Cy = 6858000 },
            new NotesSize { Cx = 6858000, Cy = 9144000 });
    }

    // 슬라이드 기하(EMU). 9144000×6858000 = 4:3 기본.
    private const long SlideW = 9144000;      // 4:3 슬라이드 폭
    private const long SlideH = 6858000;      // 4:3 슬라이드 높이
    private const long LeftBarW = 110000;     // 좌측 accent 세로 바 폭
    private const long MarginX = 685800;      // 0.75"
    private const long ContentW = 7772400;    // 슬라이드 폭 - 좌우 여백
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
    private static Slide BuildSlide(SlideIn s, ThemePreset p)
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

        // 본문 폭: text_image 는 좌측 절반(우측은 이미지 자리).
        long bodyW = layout == "text_image" ? (SlideW / 2) - MarginX : ContentW;
        long bodyH = BodyBottom - bodyTop;

        if (hasCols)
        {
            var cols = s.Columns!.Take(2).ToList();
            const long gap = 304800;
            var colW = (ContentW - gap) / 2;
            for (var i = 0; i < cols.Count; i++)
            {
                var x = MarginX + i * (colW + gap);
                var paras = new List<D.Paragraph>();
                if (!string.IsNullOrWhiteSpace(cols[i].Heading))
                {
                    paras.Add(TextParagraph(cols[i].Heading!, p.SubtitlePt * 100, bold: true, bullet: false, color: p.AccentHex, fontName: p.BodyFont));
                }

                var colSize = Math.Min(p.BodyPt * 100, BulletFontSize((cols[i].Bullets ?? new List<string>()).Count));
                foreach (var b in cols[i].Bullets ?? new List<string>())
                {
                    paras.Add(TextParagraph(b, colSize, bold: false, bullet: true, color: p.BodyHex, fontName: p.BodyFont));
                }

                if (paras.Count == 0)
                {
                    paras.Add(TextParagraph(string.Empty, p.BodyPt * 100, false, false, null));
                }

                tree.AppendChild(MakeShape(id++, $"Col{i + 1}", x, bodyTop, colW, bodyH, paras));
            }
        }
        else if (hasBullets)
        {
            var size = Math.Min(p.BodyPt * 100, BulletFontSize(s.Bullets!.Count));
            tree.AppendChild(MakeShape(id++, "Body", MarginX, bodyTop, bodyW, bodyH,
                s.Bullets!.Select(b => TextParagraph(b, size, bold: false, bullet: true, color: p.BodyHex, fontName: p.BodyFont))));
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
    private static P.Shape MakeShape(
        uint id, string name, long x, long y, long cx, long cy, IEnumerable<D.Paragraph> paragraphs)
    {
        // normAutofit: 텍스트가 상자를 넘치면 PowerPoint 가 폰트를 자동 축소(오버플로우 방지).
        var body = new P.TextBody(new D.BodyProperties(new D.NormalAutoFit()), new D.ListStyle());
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

    private static D.Paragraph TextParagraph(string text, int fontSize, bool bold, bool bullet, string? color, string? fontName = null)
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

        var para = new D.Paragraph();
        // 본문 불릿: 줄간격 여유(120%)로 매달린 줄·과밀 완화. 불릿 색은 본문 색에 맞춘다
        // (다크 배경에서 검정 불릿이 안 보이는 것 방지). 자식 순서: lnSpc → buClr → buFont → buChar.
        D.ParagraphProperties pPr;
        if (bullet)
        {
            pPr = new D.ParagraphProperties(new D.LineSpacing(new D.SpacingPercent { Val = 120000 }));
            if (color is not null)
            {
                pPr.AppendChild(new D.BulletColor(new D.RgbColorModelHex { Val = color }));
            }

            pPr.AppendChild(new D.BulletFont { Typeface = "Arial" });
            pPr.AppendChild(new D.CharacterBullet { Char = "•" });
        }
        else
        {
            pPr = new D.ParagraphProperties(new D.NoBullet());
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

        var fontScheme = new D.FontScheme(
            new D.MajorFont(new D.LatinFont { Typeface = "Calibri Light" }, new D.EastAsianFont { Typeface = string.Empty }, new D.ComplexScriptFont { Typeface = string.Empty }),
            new D.MinorFont(new D.LatinFont { Typeface = "Calibri" }, new D.EastAsianFont { Typeface = string.Empty }, new D.ComplexScriptFont { Typeface = string.Empty }))
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
