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
        dependencies, no PowerPoint install. Each slide supports a title, single-column bullets, a
        TWO-COLUMN layout (`columns`), a TABLE (`table`: headers + rows), and an ACCENT color
        (`accent`, hex like #2F5496) applied to the title, an underline bar, and the table header row.
        Layout is automatic: title on top, text (bullets/columns) then table stacked below. For diagrams,
        `shapes` places free-form boxes/arrows (rect/roundRect/ellipse/arrow/chevron/diamond) at inch
        coordinates (slide is 10 x 7.5) with fill + centered label. ALWAYS use this to produce a .pptx
        file. Do NOT install packages (pptxgenjs, python-pptx, etc.) or write scripts. For editing an
        OPEN presentation on Windows, use PowerPointEdit.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Output .pptx path (relative to workspace)" },
            "slides": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "accent": { "type": "string", "description": "Accent color hex (e.g. #2F5496) for title, underline bar, table header" },
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
        [property: JsonPropertyName("accent")] string? Accent,
        [property: JsonPropertyName("bullets")] List<string>? Bullets,
        [property: JsonPropertyName("columns")] List<ColumnIn>? Columns,
        [property: JsonPropertyName("table")] TableIn? Table,
        [property: JsonPropertyName("shapes")] List<ShapeIn>? Shapes,
        [property: JsonPropertyName("image")] ImageIn? Image);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("slides")] List<SlideIn>? Slides);

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
            Write(full, inp.Slides, context.WorkingDirectory);
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

    private static void Write(string path, List<SlideIn> slides, string workingDir)
    {
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presPart = doc.AddPresentationPart();
        presPart.Presentation = new P.Presentation();

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

        var slideIdList = new SlideIdList();
        uint slideId = 256;
        foreach (var s in slides)
        {
            var slidePart = presPart.AddNewPart<SlidePart>();
            slidePart.Slide = BuildSlide(s);
            slidePart.AddPart(layoutPart);

            if (s.Image is { Path: { } imgPath } && !string.IsNullOrWhiteSpace(imgPath))
            {
                var imgFull = OpenXmlPaths.ResolveForRead(workingDir, imgPath);
                if (!File.Exists(imgFull))
                {
                    throw new FileNotFoundException($"이미지 없음: {imgPath}");
                }

                var width = s.Image.WidthInches is > 0 ? s.Image.WidthInches!.Value : 4.0;
                var (cx, cy) = ImageEmbed.EmuSize(imgFull, width);
                var x = s.Image.X is { } xv ? (long)(xv * ImageEmbed.EmuPerInch) : (9144000 - cx) / 2;
                var y = s.Image.Y is { } yv ? (long)(yv * ImageEmbed.EmuPerInch) : (long)(2.2 * ImageEmbed.EmuPerInch);
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
    private const long MarginX = 685800;      // 0.75"
    private const long ContentW = 7772400;    // 슬라이드 폭 - 좌우 여백
    private const long BodyTop = 1500000;
    private const long BodyBottom = 6500000;
    private const long RowHeight = 370840;    // 표 행 높이 ≈ 0.4"
    private const string DefaultAccent = "2F5496";

    private static Slide BuildSlide(SlideIn s)
    {
        var tree = new ShapeTree(NvGroupShapeProps(), new GroupShapeProperties());
        uint id = 2;
        var accent = ParseColor(s.Accent) ?? DefaultAccent;
        var hasTitle = !string.IsNullOrWhiteSpace(s.Title);

        if (hasTitle)
        {
            tree.AppendChild(MakeShape(id++, "Title", MarginX, 381000, ContentW, 900000,
                new[] { TextParagraph(s.Title!, 3200, bold: true, bullet: false, color: accent) }));
            tree.AppendChild(AccentBar(id++, MarginX, 1330000, ContentW, 50000, accent)); // 밑줄 강조바
        }

        var hasCols = s.Columns is { Count: > 0 };
        var hasBullets = s.Bullets is { Count: > 0 };
        var hasText = hasCols || hasBullets;
        var hasTable = s.Table is not null && ((s.Table.Headers?.Count ?? 0) > 0 || (s.Table.Rows?.Count ?? 0) > 0);

        // 본문/표 영역 분할: 둘 다 있으면 텍스트 위·표 아래로 스택.
        long textTop = BodyTop, textH = BodyBottom - BodyTop;
        long tableTop = BodyTop;
        if (hasText && hasTable)
        {
            textH = 2200000;
            tableTop = BodyTop + textH + 200000;
        }

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
                    paras.Add(TextParagraph(cols[i].Heading!, 2000, bold: true, bullet: false, color: accent));
                }

                foreach (var b in cols[i].Bullets ?? new List<string>())
                {
                    paras.Add(TextParagraph(b, 1600, bold: false, bullet: true, color: null));
                }

                if (paras.Count == 0)
                {
                    paras.Add(TextParagraph(string.Empty, 1600, bold: false, bullet: false, color: null));
                }

                tree.AppendChild(MakeShape(id++, $"Col{i + 1}", x, textTop, colW, textH, paras));
            }
        }
        else if (hasBullets)
        {
            tree.AppendChild(MakeShape(id++, "Body", MarginX, textTop, ContentW, textH,
                s.Bullets!.Select(b => TextParagraph(b, 1800, bold: false, bullet: true, color: null))));
        }

        if (hasTable)
        {
            tree.AppendChild(BuildTable(id++, MarginX, tableTop, ContentW, s.Table!, accent));
        }

        var hasShapes = s.Shapes is { Count: > 0 };
        if (hasShapes)
        {
            foreach (var sh in s.Shapes!)
            {
                tree.AppendChild(CustomShape(id++, sh));
            }
        }

        if (!hasTitle && !hasText && !hasTable && !hasShapes)
        {
            tree.AppendChild(MakeShape(id, "Body", MarginX, BodyTop, ContentW, BodyBottom - BodyTop,
                new[] { TextParagraph(string.Empty, 1800, bold: false, bullet: false, color: null) }));
        }

        return new Slide(new CommonSlideData(tree), new ColorMapOverride(new D.MasterColorMapping()));
    }

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

    private static D.Paragraph CenteredText(string text, int fontSizePt, bool bold, string? color)
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
        var body = new P.TextBody(new D.BodyProperties(), new D.ListStyle());
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

    private static D.Paragraph TextParagraph(string text, int fontSize, bool bold, bool bullet, string? color)
    {
        var runProps = new D.RunProperties { Language = "en-US", FontSize = fontSize };
        if (bold)
        {
            runProps.Bold = true;
        }

        if (color is not null)
        {
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        }

        var para = new D.Paragraph();
        para.AppendChild(bullet
            ? new D.ParagraphProperties(new D.BulletFont { Typeface = "Arial" }, new D.CharacterBullet { Char = "•" })
            : new D.ParagraphProperties(new D.NoBullet()));
        para.AppendChild(new D.Run(runProps, new D.Text(text)));
        return para;
    }

    // 표(graphicFrame + a:tbl). 헤더 행은 accent 배경 + 흰 볼드.
    private static P.GraphicFrame BuildTable(uint id, long x, long y, long w, TableIn t, string accent)
    {
        var headers = t.Headers ?? new List<string>();
        var rows = t.Rows ?? new List<List<string>>();
        var ncols = Math.Max(headers.Count, rows.Count > 0 ? rows.Max(r => r.Count) : 0);
        if (ncols == 0)
        {
            ncols = 1;
        }

        var colW = w / ncols;

        var table = new D.Table(new D.TableProperties { FirstRow = true });
        var grid = new D.TableGrid();
        for (var c = 0; c < ncols; c++)
        {
            grid.AppendChild(new D.GridColumn { Width = colW });
        }

        table.AppendChild(grid);

        if (headers.Count > 0)
        {
            table.AppendChild(BuildRow(headers, ncols, header: true, accent));
        }

        foreach (var r in rows)
        {
            table.AppendChild(BuildRow(r, ncols, header: false, accent));
        }

        var totalH = RowHeight * ((headers.Count > 0 ? 1 : 0) + rows.Count);
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

    private static D.TableRow BuildRow(List<string> cells, int ncols, bool header, string accent)
    {
        var tr = new D.TableRow { Height = RowHeight };
        for (var c = 0; c < ncols; c++)
        {
            tr.AppendChild(BuildCell(c < cells.Count ? cells[c] : string.Empty, header, accent));
        }

        return tr;
    }

    private static D.TableCell BuildCell(string text, bool header, string accent)
    {
        var runProps = new D.RunProperties { Language = "en-US", FontSize = header ? 1600 : 1400 };
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
