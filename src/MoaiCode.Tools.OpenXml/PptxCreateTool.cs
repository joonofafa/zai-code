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
        Creates a new PowerPoint presentation (.pptx) from slides (each a title + bullet lines)
        using the built-in Open XML writer — no dependencies, no PowerPoint install. ALWAYS use this
        to produce a .pptx file. Do NOT install packages (npm 'pptxgenjs', python-pptx, etc.) or
        write scripts to build presentations. For editing an OPEN presentation on Windows, use PowerPointEdit.
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
                  "bullets": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          },
          "required": ["path", "slides"]
        }
        """).RootElement.Clone();

    private sealed record SlideIn(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("bullets")] List<string>? Bullets);

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
            Write(full, inp.Slides);
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

    private static void Write(string path, List<SlideIn> slides)
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
    private const long MarginX = 685800;   // 0.75"
    private const long ContentW = 7772400; // 슬라이드 폭 - 좌우 여백

    private static Slide BuildSlide(SlideIn s)
    {
        var tree = new ShapeTree(NvGroupShapeProps(), new GroupShapeProperties());
        uint id = 2;
        var hasTitle = !string.IsNullOrWhiteSpace(s.Title);

        if (hasTitle)
        {
            tree.AppendChild(MakeShape(id++, "Title", MarginX, 381000, ContentW, 1000000,
                new[] { TextParagraph(s.Title!, 3200, bold: true, bullet: false) }));
        }

        var bullets = (s.Bullets ?? new List<string>()).Where(b => b is not null).ToList();
        if (bullets.Count > 0)
        {
            var y = hasTitle ? 1600200L : 685800L;
            tree.AppendChild(MakeShape(id++, "Body", MarginX, y, ContentW, 4800000,
                bullets.Select(b => TextParagraph(b, 1800, bold: false, bullet: true))));
        }
        else if (!hasTitle)
        {
            tree.AppendChild(MakeShape(id, "Body", MarginX, 685800, ContentW, 4800000,
                new[] { TextParagraph(string.Empty, 1800, bold: false, bullet: false) }));
        }

        return new Slide(new CommonSlideData(tree), new ColorMapOverride(new D.MasterColorMapping()));
    }

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

    private static D.Paragraph TextParagraph(string text, int fontSize, bool bold, bool bullet)
    {
        var runProps = new D.RunProperties { Language = "en-US", FontSize = fontSize };
        if (bold)
        {
            runProps.Bold = true;
        }

        var para = new D.Paragraph();
        para.AppendChild(bullet
            ? new D.ParagraphProperties(new D.BulletFont { Typeface = "Arial" }, new D.CharacterBullet { Char = "•" })
            : new D.ParagraphProperties(new D.NoBullet()));
        para.AppendChild(new D.Run(runProps, new D.Text(text)));
        return para;
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
