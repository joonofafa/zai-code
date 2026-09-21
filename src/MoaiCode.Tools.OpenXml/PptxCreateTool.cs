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
            "accent": { "type": "string", "description": "Brand accent color hex (from a template style skill). When any of accent/bg/text_color/title_font/body_font is given, the deck uses these brand values instead of the A/B/C/D preset." },
            "bg": { "type": "string", "description": "Brand background color hex (from a template style skill)." },
            "text_color": { "type": "string", "description": "Brand body/title text color hex (from a template style skill)." },
            "title_font": { "type": "string", "description": "Brand title font name (from a template style skill)." },
            "body_font": { "type": "string", "description": "Brand body font name (from a template style skill)." },
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

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("template")] string? Template,
        // 브랜드 테마(업로드 템플릿 스타일 스킬에서 전달). 하나라도 있으면 코드 프리셋 대신 이 값으로 스타일.
        [property: JsonPropertyName("accent")] string? Accent,
        [property: JsonPropertyName("bg")] string? Bg,
        [property: JsonPropertyName("text_color")] string? TextColor,
        [property: JsonPropertyName("title_font")] string? TitleFont,
        [property: JsonPropertyName("body_font")] string? BodyFont,
        [property: JsonPropertyName("slides")] List<PptxDesign.SlideSpec>? Slides);

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
            // 브랜드 테마 값이 하나라도 오면 데이터-테마(FromTheme), 아니면 코드 프리셋(A/B/C/D).
            var hasTheme = inp.Accent is not null || inp.Bg is not null || inp.TextColor is not null
                           || inp.TitleFont is not null || inp.BodyFont is not null;
            var preset = hasTheme
                ? PptxDesign.FromTheme(new TemplateTheme(
                    AccentHex: inp.Accent, BgHex: inp.Bg, TextHex: inp.TextColor,
                    TitleFontLatin: inp.TitleFont, TitleFontEa: inp.TitleFont,
                    BodyFontLatin: inp.BodyFont, BodyFontEa: inp.BodyFont))
                : PptxDesign.ResolveTemplate(inp.Template);

            // 앱 언어가 동아시아(ko/ja/zh)면 문서 기본 폰트를 그 언어 폰트(예: Malgun Gothic)로 통일한다.
            // 라틴 기본값(Calibri Light/Calibri)이 숫자·영문에 남아 이질적으로 보이던 문제 해소.
            // 사용자가 브랜드 폰트를 명시(inp.TitleFont/BodyFont)한 경우는 그 폰트를 존중.
            var docEaFont = FontResolver.AppDefaultEastAsianFont();
            if (docEaFont is not null)
            {
                preset = preset with
                {
                    TitleFont = inp.TitleFont ?? docEaFont,
                    BodyFont = inp.BodyFont ?? docEaFont,
                };
            }

            issues = Write(full, preset, inp.Slides, context.WorkingDirectory, docEaFont);
            // 문서가 자기 테마를 알게 스탬프 — COM 편집 툴(insert_table/add_designed_slide)이 기본값으로 읽는다.
            DocThemeStamp.Stamp(full, new DocTheme(preset.AccentHex, preset.BgHex, preset.BodyHex, preset.TitleFont, preset.BodyFont));
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

    private static List<PptxLayoutCheck.Issue> Write(string path, PptxDesign.ThemePreset preset, List<PptxDesign.SlideSpec> slides, string workingDir, string? themeEaFont = null)
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
        themePart.Theme = MinimalTheme(themeEaFont);

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
                    : isTextImage ? PptxDesign.SlideW - cx - PptxDesign.MarginX  // 우측 정렬
                    : (PptxDesign.SlideW - cx) / 2;                              // 중앙
                var y = s.Image.Y is { } yv ? (long)(yv * ImageEmbed.EmuPerInch)
                    : isTextImage ? PptxDesign.BodyTop + 300000                  // 본문 상단 맞춤
                    : (long)(2.2 * ImageEmbed.EmuPerInch);
                var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;
                tree.AppendChild(ImageEmbed.PptxPicture(slidePart, imgFull, x, y, cx, cy, 900U + slideId));
            }

            // 기하 QA — 텍스트 겹침/화면밖 검출(장식/배경 제외). 비-치명, 경고만 수집.
            issues.AddRange(PptxLayoutCheck.Inspect(slidePart.Slide, slideNo, PptxDesign.SlideW, PptxDesign.SlideH));

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
            new SlideSize { Cx = (int)PptxDesign.SlideW, Cy = (int)PptxDesign.SlideH },
            new NotesSize { Cx = 6858000, Cy = 9144000 });

        return issues;
    }

    // 템플릿(디자인 프리셋) × 레이아웃(객체 배치)로 슬라이드를 만든다.
    // 배치·색·타이포는 백엔드 무관 플래너 PptxDesign.Plan 이 씬 op 로 규정하고,
    // 여기서는 각 op 를 Open XML 도형으로 렌더한다(COM 렌더러와 동일한 씬을 공유 — DRY).
    private static Slide BuildSlide(PptxDesign.SlideSpec s, PptxDesign.ThemePreset p, int index, int total)
    {
        var tree = new ShapeTree(NvGroupShapeProps(), new GroupShapeProperties());
        uint id = 2;
        foreach (var op in PptxDesign.Plan(s, p, index, total))
        {
            tree.AppendChild(RenderOp(op, ref id));
        }

        return WrapSlide(tree);
    }

    // 씬 op → Open XML 도형(op 당 도형 1개, id 순차 부여 — 기존 출력과 동일 순서).
    private static OpenXmlElement RenderOp(PptxDesign.SceneOp op, ref uint id) => op switch
    {
        PptxDesign.BarOp b => AccentBar(id++, b.X, b.Y, b.W, b.H, b.FillHex),
        PptxDesign.PanelOp pn => Panel(id++, pn.X, pn.Y, pn.W, pn.H, pn.FillHex, pn.BorderHex),
        PptxDesign.EllipseOp e => Circle(id++, e.X, e.Y, e.D, e.FillHex, e.Text, e.FontPt, e.FontColorHex),
        PptxDesign.TextOp t => MakeShape(id++, t.Name, t.X, t.Y, t.W, t.H, t.Paras.Select(ParaToDrawing), MapAnchor(t.Anchor)),
        PptxDesign.TableOp tb => BuildTable(id++, tb.X, tb.Y, tb.W, tb.Headers, tb.Rows, tb.AccentHex, tb.AvailHeight),
        PptxDesign.FreeShapeOp f => CustomShape(id++, f.Spec),
        _ => throw new NotSupportedException($"Unknown scene op: {op.GetType().Name}"),
    };

    private static D.TextAnchoringTypeValues? MapAnchor(PptxDesign.Anchor a) =>
        a == PptxDesign.Anchor.Center ? D.TextAnchoringTypeValues.Center : null;

    // ParaSpec → a:p. CenteredText/AlignedText/TextParagraph 를 통합(불릿·정렬·앞여백·EA폰트).
    private static D.Paragraph ParaToDrawing(PptxDesign.ParaSpec ps)
    {
        var runProps = new D.RunProperties { FontSize = ps.FontHundredths };
        if (ps.Bold)
        {
            runProps.Bold = true;
        }

        // RunProperties 자식 순서(스키마): fill(SolidFill) → latin(LatinFont) → ea(EastAsianFont).
        if (ps.ColorHex is not null)
        {
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = ps.ColorHex }));
        }

        ApplyRunScript(runProps, ps.Text, ps.FontName);

        var para = new D.Paragraph();
        D.ParagraphProperties pPr;
        if (ps.Bullet)
        {
            // 불릿: 줄간격 여유(120%) + 앞여백(적을수록 크게) + 본문색 불릿. 자식 순서: lnSpc → spcBef → buClr → buFont → buChar.
            pPr = new D.ParagraphProperties(new D.LineSpacing(new D.SpacingPercent { Val = 120000 }));
            if (ps.SpaceBeforePct > 0)
            {
                pPr.AppendChild(new D.SpaceBefore(new D.SpacingPercent { Val = ps.SpaceBeforePct }));
            }

            if (ps.ColorHex is not null)
            {
                pPr.AppendChild(new D.BulletColor(new D.RgbColorModelHex { Val = ps.ColorHex }));
            }

            pPr.AppendChild(new D.BulletFont { Typeface = "Arial" });
            pPr.AppendChild(new D.CharacterBullet { Char = "•" });
        }
        else
        {
            pPr = new D.ParagraphProperties();
            if (ps.SpaceBeforePct > 0)
            {
                pPr.AppendChild(new D.SpaceBefore(new D.SpacingPercent { Val = ps.SpaceBeforePct }));
            }

            if (ps.Align != PptxDesign.Align.Left)
            {
                pPr.Alignment = ps.Align == PptxDesign.Align.Center
                    ? D.TextAlignmentTypeValues.Center
                    : D.TextAlignmentTypeValues.Right;
            }

            pPr.AppendChild(new D.NoBullet());
        }

        para.AppendChild(pPr);
        para.AppendChild(new D.Run(runProps, new D.Text(ps.Text)));
        return para;
    }

    // 런에 스크립트별 폰트/언어를 적용(세 런 빌더 공용, DRY). 프루핑 언어 태그 + Latin(선택) + EA(감지 시).
    // 자식 순서(스키마) fill→latin→ea 를 지키려면 호출 전에 SolidFill 을 먼저 append 해야 한다.
    private static void ApplyRunScript(D.RunProperties runProps, string? text, string? latinFont)
    {
        var lang = FontResolver.DetectLang(text);
        runProps.Language = FontResolver.BcpTag(lang);
        if (!string.IsNullOrEmpty(latinFont))
        {
            runProps.AppendChild(new D.LatinFont { Typeface = latinFont });
        }

        // 런이 명시적 Latin 폰트를 가지면 테마 EA 로 폴백되지 않으므로, EA 감지 시 직접 지정.
        if (lang is not null)
        {
            runProps.AppendChild(new D.EastAsianFont { Typeface = FontResolver.ResolveEastAsian(lang) });
        }
    }

    private static Slide WrapSlide(ShapeTree tree) =>
        new(new CommonSlideData(tree), new ColorMapOverride(new D.MasterColorMapping()));

    private const long EmuPerInch = 914400;

    // 자유 도형(다이어그램용). 좌표는 인치 → EMU. 선택적 채우기·중앙정렬 텍스트.
    private static P.Shape CustomShape(uint id, PptxDesign.ShapeSpec sh)
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

    // 중앙 정렬 단일 문단(배지·자유도형 라벨). 씬 렌더 경로는 ParaToDrawing 을 쓰고,
    // Circle/CustomShape 처럼 직접 만드는 곳만 이 헬퍼를 쓴다.
    private static D.Paragraph CenteredText(string text, int fontSizePt, bool bold, string? color, string? fontName = null)
    {
        var runProps = new D.RunProperties { FontSize = fontSizePt * 100 };
        if (bold)
        {
            runProps.Bold = true;
        }

        if (color is not null)
        {
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        }

        ApplyRunScript(runProps, text, fontName);

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
    private static P.Shape Panel(uint id, long x, long y, long w, long h, string color, string borderColor)
    {
        var geo = new D.PresetGeometry(
            new D.AdjustValueList(new D.ShapeGuide { Name = "adj", Formula = "val 4200" }))
        { Preset = D.ShapeTypeValues.RoundRectangle };
        var spPr = new P.ShapeProperties(
            new D.Transform2D(new D.Offset { X = x, Y = y }, new D.Extents { Cx = w, Cy = h }),
            geo,
            new D.SolidFill(new D.RgbColorModelHex { Val = color }));
        // 얇은 테두리(0.75pt) — 카드가 배경과 구분되어 '정의된 카드'로 보이게. spPr 순서상 ln 은 fill 뒤.
        spPr.AppendChild(new D.Outline(new D.SolidFill(new D.RgbColorModelHex { Val = borderColor })) { Width = 9525 });
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Panel" },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            spPr,
            new P.TextBody(new D.BodyProperties(), new D.ListStyle(), new D.Paragraph()));
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

    // 표(graphicFrame + a:tbl). 헤더 행은 accent 배경 + 흰 볼드.
    // availHeight: 표에 허용된 세로 공간. 행이 많으면 행 높이·폰트를 줄여 슬라이드 밖으로 넘치지 않게 한다.
    private static P.GraphicFrame BuildTable(uint id, long x, long y, long w, List<string> headers, List<List<string>> rows, string accent, long availHeight)
    {
        var ncols = Math.Max(headers.Count, rows.Count > 0 ? rows.Max(r => r.Count) : 0);
        if (ncols == 0)
        {
            ncols = 1;
        }

        var colW = w / ncols;

        // 행 높이 적응: 가용 높이/행수. 기본보다 크게는 안 늘리고, 하한 아래로는 안 줄인다.
        var nrows = (headers.Count > 0 ? 1 : 0) + rows.Count;
        var rowH = nrows > 0
            ? Math.Max(PptxDesign.MinRowHeight, Math.Min(PptxDesign.RowHeight, availHeight / nrows))
            : PptxDesign.RowHeight;
        // 행 높이가 기본보다 작아지면 폰트도 같은 비율로 축소(하한 있음).
        var fontScale = (double)rowH / PptxDesign.RowHeight;

        // No Style, No Grid 스타일 — 기본 격자선(스프레드시트 느낌)을 없애고, 밴딩·여백으로 깔끔하게.
        var table = new D.Table(
            new D.TableProperties(new D.TableStyleId("{2D5ABB26-0587-4C30-8999-92F81FD0307C}")) { FirstRow = true });
        var grid = new D.TableGrid();
        for (var c = 0; c < ncols; c++)
        {
            grid.AppendChild(new D.GridColumn { Width = colW });
        }

        table.AppendChild(grid);

        if (headers.Count > 0)
        {
            table.AppendChild(BuildRow(headers, ncols, header: true, accent, rowH, fontScale, 0));
        }

        for (var ri = 0; ri < rows.Count; ri++)
        {
            table.AppendChild(BuildRow(rows[ri], ncols, header: false, accent, rowH, fontScale, ri));
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

    private static D.TableRow BuildRow(List<string> cells, int ncols, bool header, string accent, long rowH, double fontScale, int rowIndex)
    {
        var tr = new D.TableRow { Height = rowH };
        for (var c = 0; c < ncols; c++)
        {
            tr.AppendChild(BuildCell(c < cells.Count ? cells[c] : string.Empty, header, accent, fontScale, rowIndex));
        }

        return tr;
    }

    private static D.TableCell BuildCell(string text, bool header, string accent, double fontScale, int rowIndex)
    {
        var baseSize = header ? 1600 : 1400;
        var size = Math.Max(900, (int)(baseSize * fontScale)); // 축소 시 하한 9pt
        var runProps = new D.RunProperties { FontSize = size };
        if (header)
        {
            runProps.Bold = true;
            runProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = "FFFFFF" }));
        }

        // Latin=null → 테마 minor Latin 상속(앱 언어=EA 면 그 폰트). EA 글리프는 스크립트 감지로 직접 지정.
        ApplyRunScript(runProps, text, null);

        var body = new D.TextBody(
            new D.BodyProperties(),
            new D.ListStyle(),
            new D.Paragraph(new D.ParagraphProperties(new D.NoBullet()), new D.Run(runProps, new D.Text(text))));

        // 여백(패딩)으로 숨통 + 세로 중앙 정렬.
        var cellProps = new D.TableCellProperties
        {
            LeftMargin = 128016,
            RightMargin = 128016,
            TopMargin = 54000,
            BottomMargin = 54000,
            Anchor = D.TextAnchoringTypeValues.Center,
        };
        // 세로선·상단선 제거(NoFill), 가로 구분선만(본문 하단 light). 격자 대신 헤더강조+가로줄로 깔끔하게.
        // tcPr 셀 테두리는 a:lnL/lnR/lnT/lnB (= *BorderLineProperties, CT_LineProperties). 순서: L→R→T→B→fill.
        cellProps.AppendChild(new D.LeftBorderLineProperties(new D.NoFill()));
        cellProps.AppendChild(new D.RightBorderLineProperties(new D.NoFill()));
        cellProps.AppendChild(new D.TopBorderLineProperties(new D.NoFill()));
        cellProps.AppendChild(header
            ? new D.BottomBorderLineProperties(new D.NoFill())
            : new D.BottomBorderLineProperties(new D.SolidFill(new D.RgbColorModelHex { Val = "E1E5EA" })) { Width = 6350 });
        cellProps.AppendChild(new D.SolidFill(new D.RgbColorModelHex { Val = header ? accent : "FFFFFF" }));

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
    // themeEaFont 가 있으면(앱 언어=ko/ja/zh) major/minor 를 그 폰트로 통일 — Latin·EA 모두.
    private static D.Theme MinimalTheme(string? themeEaFont = null)
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
        // 앱 언어가 EA 면 Latin 까지 그 폰트로 통일(themeEaFont), 아니면 라틴 기본값 유지.
        var majorLatin = themeEaFont ?? "Calibri Light";
        var minorLatin = themeEaFont ?? "Calibri";
        var eaTypeface = themeEaFont ?? string.Empty;
        var fontScheme = new D.FontScheme(
            new D.MajorFont(new D.LatinFont { Typeface = majorLatin }, new D.EastAsianFont { Typeface = eaTypeface }, new D.ComplexScriptFont { Typeface = string.Empty }),
            new D.MinorFont(new D.LatinFont { Typeface = minorLatin }, new D.EastAsianFont { Typeface = eaTypeface }, new D.ComplexScriptFont { Typeface = string.Empty }))
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
