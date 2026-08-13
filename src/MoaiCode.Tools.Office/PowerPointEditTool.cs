using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 활성 PowerPoint 의 도형을 변경한다(쓰기). 편집 툴의 대표 패턴:
///  - IsReadOnly=false → 쓰기 권한 게이트를 통과한다(ModeAwarePermissionGate).
///  - 쓰기 직전에 대상(프레젠테이션·슬라이드·도형)이 여전히 유효한지 재확인한다.
///    조회 시점과 실행 시점 사이에 사용자가 슬라이드/선택을 바꿀 수 있기 때문(원본 설계 §상태 식별).
/// 액션: set_text(텍스트), set_fill(채우기 색), set_font(글자 색·크기·굵기), set_line(테두리 색·두께).
/// 대상: slide_index + shape_id/shape_name 지정, 또는 (미지정 시) 현재 선택한 도형 전체.
/// </summary>
public sealed class PowerPointEditTool : ITool
{
    // MsoTriState.
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    // PpSelectionType.
    private const int PpSelectionShapes = 2;
    private const int PpSelectionText = 3;

    // PpFixedFormatType.ppFixedFormatTypePDF.
    private const int PpFixedFormatTypePDF = 2;

    // PpBorderType: 셀 4변.
    private static readonly int[] PpCellBorders = { 1, 2, 3, 4 }; // top, left, bottom, right

    private readonly StaDispatcher _sta;

    public PowerPointEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "PowerPointEdit";

    public string Description => """
        Edits the running PowerPoint's active presentation (write). Actions:
          - set_text: set a shape's text (needs "text")
          - set_fill: set a shape's fill (background) color (needs "color")
          - set_font: set text color/size/bold (any of "color", "font_size", "bold")
          - set_line: set border line color/weight (any of "color", "line_weight")
          - set_geometry: move/resize/rotate/flip a shape (any of "left","top","width","height" in points,
            "rotation" in degrees clockwise, "flip": horizontal|vertical)
          - insert_picture: add an image from a local file ("path"; optional "left","top","width","height"
            in points, omit width/height for native size) onto slide_index (or the current slide). Get the
            file first via ImageCreate (generated) or ImageFetch (from a web URL).
          - add_slide: append a new slide (optionally at "slide_index"). Fill it directly to draft fast:
            "text" = title, "bullets" = body lines, "layout" = title_content(default)|title_only|title|blank.
            Repeat to build a deck from an empty presentation.
          - replace: find & replace ALL occurrences of "find_text" with "replace_text" across every slide.
          - export_pdf: export the presentation to PDF ("path" = output .pdf; if omitted, next to the file).
          - delete_slide: delete the slide at "slide_index".
          - delete_shape: delete the target shape (by slide_index+shape_id/shape_name, or current selection).
          - insert_table: add a table to slide_index (or the current slide) and fill it. Provide "cells" (rows
            of cell strings); size is inferred and the first row is bolded as a header. Optional
            "left"/"top"/"width"/"height" in points. Prefer this over set_text when the user wants content as a table.
            Optional "font_size" (cell font), "header_fill" (header row background), "header_color" (header text),
            "border_color" (table border line color).
          - set_cell: edit an existing table cell. Target the table shape by slide_index+shape_id (from
            PowerPointInspect) or the current selection; "row"/"col" (1-based), "text" = new cell content.
            Optional "color" (cell text color), "fill_color" (cell background), "border_color" (cell borders),
            "font_size".
          - insert_divider: add a real horizontal line shape across the slide (NOT text). On slide_index or the
            current slide; optional "top" (y), "left"/"width" (extent), "border_color", "line_weight".
          - set_background: set the SLIDE BACKGROUND fill color ("color"). Without slide_index it applies to the
            whole deck (master + all slides) — use this to apply a design theme's background. This is the ONLY
            way to color the slide background; set_fill only colors shapes, not the background.
          - new_presentation: start a brand-new presentation with one blank title slide (launches PowerPoint
            if not running) so you can then build it with add_slide/set_text/etc. Use this when none is open.
          - apply_template: apply a design template's THEME (color scheme, fonts, slide master & layouts) from
            a .pptx or .potx file ("path") to the WHOLE active presentation. This is the deterministic way to
            "apply the attached deck's theme" — do NOT web-search or hand-recolor shapes;
            just open/select the target deck and call apply_template with the attached file's path.
        Target the shape by shape_id (from PowerPointInspect) on slide_index; shape_name is a fallback.
        If no shape target is given, the action applies to the CURRENTLY SELECTED shape(s).
        "scope" selects where the shape lives: "slide" (default, body shapes), "layout" (the slide's
        layout background shapes), or "master" (the slide master's background shapes). Use layout/master
        to recolor template decorations (banners, sidebars) that stay unchanged when only body shapes are
        edited — e.g. an overall theme color change. NOTE: editing a master/layout shape affects EVERY
        slide sharing it. Colors are "#RRGGBB" hex or a basic name (red, blue, green, yellow, black,
        white, ...). Re-verifies targets immediately before writing. Windows only.
        """;

    public bool IsReadOnly => false;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set_text", "set_fill", "set_font", "set_line", "set_geometry", "insert_picture", "add_slide", "replace", "export_pdf", "delete_slide", "delete_shape", "new_presentation", "insert_table", "set_cell", "insert_divider", "set_background", "apply_template"], "description": "Edit action" },
            "rows": { "type": "integer", "description": "insert_table row count (or inferred from cells)" },
            "cols": { "type": "integer", "description": "insert_table column count (or inferred from cells)" },
            "row": { "type": "integer", "description": "1-based cell row (set_cell)" },
            "col": { "type": "integer", "description": "1-based cell column (set_cell)" },
            "fill_color": { "type": "string", "description": "Cell background color, #RRGGBB or name (set_cell)" },
            "header_fill": { "type": "string", "description": "Header row background color (insert_table)" },
            "header_color": { "type": "string", "description": "Header row text color (insert_table)" },
            "border_color": { "type": "string", "description": "Table/cell border line color (insert_table/set_cell)" },
            "cells": { "type": "array", "items": { "type": "array", "items": { "type": "string" } }, "description": "insert_table content: rows of cell strings. Table size inferred; first row bolded as header. Placed on slide_index (or current slide); optional left/top/width/height in points." },
            "find_text": { "type": "string", "description": "Text to find (replace)" },
            "replace_text": { "type": "string", "description": "Replacement text (replace)" },
            "bullets": { "type": "array", "items": { "type": "string" }, "description": "add_slide: body bullet lines" },
            "layout": { "type": "string", "enum": ["title_content", "title_only", "title", "blank"], "description": "add_slide: slide layout (default title_content)" },
            "path": { "type": "string", "description": "Local image file path (insert_picture)" },
            "slide_index": { "type": "integer", "description": "1-based slide index (omit to target current selection)" },
            "scope": { "type": "string", "enum": ["slide", "layout", "master"], "description": "Where the target shape lives: slide (default), layout, or master. layout/master require slide_index + shape_id/shape_name." },
            "shape_id": { "type": "integer", "description": "Shape id from PowerPointInspect (preferred)" },
            "shape_name": { "type": "string", "description": "Shape name (fallback if shape_id absent)" },
            "text": { "type": "string", "description": "New text (set_text)" },
            "color": { "type": "string", "description": "#RRGGBB hex or basic color name (set_fill/set_font/set_line)" },
            "font_size": { "type": "number", "description": "Font size in points (set_font)" },
            "bold": { "type": "boolean", "description": "Bold on/off (set_font)" },
            "line_weight": { "type": "number", "description": "Border line weight in points (set_line)" },
            "left": { "type": "number", "description": "X position in points (set_geometry)" },
            "top": { "type": "number", "description": "Y position in points (set_geometry)" },
            "width": { "type": "number", "description": "Width in points (set_geometry)" },
            "height": { "type": "number", "description": "Height in points (set_geometry)" },
            "rotation": { "type": "number", "description": "Rotation angle in degrees, clockwise (set_geometry)" },
            "flip": { "type": "string", "description": "Flip the shape: horizontal | vertical (set_geometry)" }
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("slide_index")] int? SlideIndex,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("shape_id")] int? ShapeId,
        [property: JsonPropertyName("shape_name")] string? ShapeName,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("font_size")] double? FontSize,
        [property: JsonPropertyName("bold")] bool? Bold,
        [property: JsonPropertyName("line_weight")] double? LineWeight,
        [property: JsonPropertyName("left")] double? Left,
        [property: JsonPropertyName("top")] double? Top,
        [property: JsonPropertyName("width")] double? Width,
        [property: JsonPropertyName("height")] double? Height,
        [property: JsonPropertyName("rotation")] double? Rotation,
        [property: JsonPropertyName("flip")] string? Flip,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("bullets")] List<string>? Bullets,
        [property: JsonPropertyName("layout")] string? Layout,
        [property: JsonPropertyName("find_text")] string? FindText,
        [property: JsonPropertyName("replace_text")] string? ReplaceText,
        [property: JsonPropertyName("rows")] int? Rows,
        [property: JsonPropertyName("cols")] int? Cols,
        [property: JsonPropertyName("cells")] List<List<string>>? Cells,
        [property: JsonPropertyName("row")] int? Row,
        [property: JsonPropertyName("col")] int? Col,
        [property: JsonPropertyName("fill_color")] string? FillColor,
        [property: JsonPropertyName("header_fill")] string? HeaderFill,
        [property: JsonPropertyName("header_color")] string? HeaderColor,
        [property: JsonPropertyName("border_color")] string? BorderColor);

    private static readonly string[] Actions =
        { "set_text", "set_fill", "set_font", "set_line", "set_geometry", "insert_picture", "add_slide", "replace", "export_pdf", "delete_slide", "delete_shape", "new_presentation", "insert_table", "set_cell", "insert_divider", "set_background", "apply_template" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput(L10n.Get("tools.pptEdit.windowsOnly"), IsError: true);
            yield break;
        }

        var inp = input.Deserialize<Input>();
        var validationError = Validate(inp);
        if (validationError is not null)
        {
            yield return new ToolOutput($"PowerPointEdit: {validationError}", IsError: true);
            yield break;
        }

        string result;
        string? error = null;
        try
        {
            result = await _sta.InvokeAsync(() => Apply(inp!, context.WorkingDirectory)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
            MoaiLog.Error($"PowerPointEdit: action={inp?.Action} threw", ex);
        }

        if (error is not null)
        {
            yield return new ToolOutput(L10n.Get("tools.pptEdit.failed", error), IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    // 입력 검증. 문제 있으면 사용자용 메시지, 없으면 null.
    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, StringComparer.Ordinal))
        {
            return L10n.Get("tools.pptEdit.invalidAction");
        }

        // 도형 지정(shape_id/shape_name) 시엔 slide_index 도 필요. 둘 다 없으면 현재 선택을 대상.
        var hasShapeRef = inp.ShapeId is not null || !string.IsNullOrWhiteSpace(inp.ShapeName);
        if (hasShapeRef && inp.SlideIndex is null)
        {
            return L10n.Get("tools.pptEdit.shapeRefNeedsSlide");
        }

        var scope = inp.Scope ?? "slide";
        if (scope is not ("slide" or "layout" or "master"))
        {
            return L10n.Get("tools.pptEdit.invalidScope");
        }

        // layout/master 도형은 현재 선택으로 못 잡으므로 명시 지정(slide_index+shape)이 필수.
        if (scope is not "slide" && !hasShapeRef)
        {
            return L10n.Get("tools.pptEdit.scopeNeedsTarget");
        }

        return inp.Action switch
        {
            "set_text" when inp.Text is null => L10n.Get("tools.pptEdit.setTextNeedsText"),
            "set_fill" when string.IsNullOrWhiteSpace(inp.Color) => L10n.Get("tools.pptEdit.setFillNeedsColor"),
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => L10n.Get("tools.pptEdit.setFontNeedsAny"),
            "set_line" when string.IsNullOrWhiteSpace(inp.Color) && inp.LineWeight is null
                => L10n.Get("tools.pptEdit.setLineNeedsAny"),
            "set_geometry" when ShapeGeometry.IsEmpty(inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip)
                => L10n.Get("tools.pptEdit.setGeometryNeedsAny"),
            "set_geometry" => ShapeGeometry.ValidateFlip(inp.Flip),
            "insert_picture" when string.IsNullOrWhiteSpace(inp.Path) => L10n.Get("tools.pptEdit.insertPictureNeedsPath"),
            "replace" when string.IsNullOrEmpty(inp.FindText) => L10n.Get("tools.pptEdit.replaceNeedsFindText"),
            "delete_slide" when inp.SlideIndex is null => L10n.Get("tools.pptEdit.deleteSlideNeedsIndex"),
            "insert_table" when (inp.Rows is null or < 1 || inp.Cols is null or < 1)
                    && (inp.Cells is null || inp.Cells.Count == 0)
                => L10n.Get("tools.pptEdit.insertTableNeedsSize"),
            "set_cell" when inp.Row is null or < 1 || inp.Col is null or < 1 || inp.Text is null
                => L10n.Get("tools.pptEdit.setCellNeedsRowColText"),
            "set_background" when string.IsNullOrWhiteSpace(inp.Color) => L10n.Get("tools.pptEdit.setBackgroundNeedsColor"),
            "apply_template" when string.IsNullOrWhiteSpace(inp.Path) => L10n.Get("tools.pptEdit.applyTemplateNeedsPath"),
            _ => null,
        };
    }

    private static string Apply(Input inp, string workingDir)
    {
        // new_presentation: 실행 중 PowerPoint 에 붙거나, 없으면 새로 띄워 빈 프레젠테이션(제목 슬라이드 1장)을 만든다.
        if (inp.Action == "new_presentation")
        {
            dynamic? papp = ComInterop.GetOrCreate("PowerPoint.Application");
            if (papp is null)
            {
                throw new InvalidOperationException(L10n.Get("tools.pptEdit.cannotStart"));
            }

            papp.Visible = MsoTrue; // PowerPoint 는 창이 보여야 조작 가능
            dynamic newPres = papp.Presentations.Add(MsoTrue);
            newPres.Slides.Add(1, PpLayoutTitle); // 빈 프레젠테이션(0장) 대신 제목 슬라이드 1장으로 시작
            return L10n.Get("tools.pptEdit.newPresentationOk");
        }

        dynamic? app = ComInterop.TryGetActiveObject("PowerPoint.Application");
        if (app is null)
        {
            throw new InvalidOperationException(L10n.Get("tools.pptEdit.notRunning"));
        }

        dynamic pres = app.ActivePresentation; // 없으면 COMException

        if (inp.Action == "insert_picture")
        {
            return InsertPicture(app, pres, inp, workingDir);
        }

        if (inp.Action == "add_slide")
        {
            return AddSlide(pres, inp);
        }

        if (inp.Action == "replace")
        {
            return Replace(pres, inp.FindText!, inp.ReplaceText ?? string.Empty);
        }

        if (inp.Action == "export_pdf")
        {
            var outPath = OfficePdf.Resolve(inp.Path, TryStr(() => (string)pres.FullName), workingDir);
            pres.ExportAsFixedFormat(outPath, PpFixedFormatTypePDF);
            return L10n.Get("tools.pptEdit.pdfExported", outPath);
        }

        if (inp.Action == "apply_template")
        {
            // 첨부 pptx/potx 의 디자인(테마·색·폰트·마스터·레이아웃)을 활성 프레젠테이션에 통째로 적용.
            // 형식을 PowerPoint 가 거부하면 가짜 성공을 만들지 않고 실제 COM 예외를 그대로 올린다.
            var tpl = System.IO.Path.GetFullPath(
                System.IO.Path.IsPathRooted(inp.Path!) ? inp.Path! : System.IO.Path.Combine(workingDir, inp.Path!));
            if (!System.IO.File.Exists(tpl))
            {
                throw new InvalidOperationException(L10n.Get("tools.pptEdit.templateFileNotFound", tpl));
            }

            pres.ApplyTemplate(tpl);
            return L10n.Get("tools.pptEdit.templateApplied", System.IO.Path.GetFileName(tpl));
        }

        if (inp.Action == "delete_slide")
        {
            int count = (int)pres.Slides.Count;
            if (inp.SlideIndex!.Value < 1 || inp.SlideIndex.Value > count)
            {
                throw new InvalidOperationException(L10n.Get("tools.pptEdit.slideNotFound", inp.SlideIndex, count));
            }

            pres.Slides[inp.SlideIndex.Value].Delete();
            return L10n.Get("tools.pptEdit.slideDeleted", inp.SlideIndex);
        }

        if (inp.Action == "insert_table")
        {
            return InsertTable(app, pres, inp);
        }

        if (inp.Action == "set_cell")
        {
            return SetCell(app, pres, inp);
        }

        if (inp.Action == "set_background")
        {
            var bg = OfficeColor.ToBgr(inp.Color!);

            void PaintSlide(dynamic s)
            {
                TrySet(() =>
                {
                    s.FollowMasterBackground = MsoFalse;
                    s.Background.Fill.Solid();
                    s.Background.Fill.ForeColor.RGB = bg;
                });
            }

            if (inp.SlideIndex is not null)
            {
                int scnt = (int)pres.Slides.Count;
                if (inp.SlideIndex.Value < 1 || inp.SlideIndex.Value > scnt)
                {
                    throw new InvalidOperationException(L10n.Get("tools.pptEdit.slideNotFound", inp.SlideIndex, scnt));
                }

                PaintSlide(pres.Slides[inp.SlideIndex.Value]);
                return L10n.Get("tools.pptEdit.slideBackgroundApplied", inp.SlideIndex);
            }

            // 전체: 마스터 + 모든 슬라이드(디자인 테마 배경).
            TrySet(() =>
            {
                pres.SlideMaster.Background.Fill.Solid();
                pres.SlideMaster.Background.Fill.ForeColor.RGB = bg;
            });
            int n = (int)pres.Slides.Count;
            for (var i = 1; i <= n; i++)
            {
                PaintSlide(pres.Slides[i]);
            }

            return L10n.Get("tools.pptEdit.allBackgroundApplied");
        }

        if (inp.Action == "insert_divider")
        {
            dynamic dslide;
            if (inp.SlideIndex is not null)
            {
                int slCount = (int)pres.Slides.Count;
                if (inp.SlideIndex.Value < 1 || inp.SlideIndex.Value > slCount)
                {
                    throw new InvalidOperationException(L10n.Get("tools.pptEdit.slideNotFound", inp.SlideIndex, slCount));
                }

                dslide = pres.Slides[inp.SlideIndex.Value];
            }
            else
            {
                dslide = app.ActiveWindow.View.Slide;
            }

            var slideW = (float)pres.PageSetup.SlideWidth;
            var x1 = (float)(inp.Left ?? 40);
            var x2 = inp.Width is not null ? x1 + (float)inp.Width.Value : slideW - 40;
            var y = (float)(inp.Top ?? 200);
            dynamic line = dslide.Shapes.AddLine(x1, y, x2, y);
            if (!string.IsNullOrWhiteSpace(inp.BorderColor))
            {
                var lc = OfficeColor.ToBgr(inp.BorderColor);
                TrySet(() => line.Line.ForeColor.RGB = lc);
            }

            if (inp.LineWeight is not null)
            {
                TrySet(() => line.Line.Weight = (float)inp.LineWeight.Value);
            }

            return L10n.Get("tools.pptEdit.dividerAdded");
        }

        var targets = ResolveTargets(app, pres, inp);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(L10n.Get("tools.pptEdit.targetShapeNotFound"));
        }

        foreach (var shape in targets)
        {
            if (inp.Action == "delete_shape")
            {
                shape.Delete();
            }
            else
            {
                ApplyToShape(shape, inp);
            }
        }

        var where = inp.SlideIndex is not null
            ? L10n.Get("tools.pptEdit.whereSlide", inp.SlideIndex) + ((inp.Scope ?? "slide") is var sc && sc != "slide" ? $"({sc})" : string.Empty)
            : L10n.Get("tools.pptEdit.whereCurrentSelection");
        var verb = inp.Action == "delete_shape" ? L10n.Get("tools.pptEdit.verbDeleteShape") : L10n.Get("tools.pptEdit.verbApply", inp.Action);
        return L10n.Get("tools.pptEdit.shapesApplied", where, targets.Count, verb);
    }

    // 로컬 이미지 파일을 슬라이드에 삽입한다. slide_index 지정 시 그 슬라이드, 없으면 현재 슬라이드.
    // 슬라이드에 표를 추가하고 cells 로 채운다. slide_index 지정 시 그 슬라이드, 없으면 현재 슬라이드.
    private static string InsertTable(dynamic app, dynamic pres, Input inp)
    {
        var cells = inp.Cells;
        int rows = inp.Rows ?? cells?.Count ?? 0;
        int cols = inp.Cols ?? (cells is { Count: > 0 } ? cells.Max(r => r.Count) : 0);
        if (rows < 1 || cols < 1)
        {
            throw new InvalidOperationException(L10n.Get("tools.pptEdit.tableSizeUnknown"));
        }

        dynamic slide;
        if (inp.SlideIndex is not null)
        {
            int slideCount = (int)pres.Slides.Count;
            if (inp.SlideIndex.Value < 1 || inp.SlideIndex.Value > slideCount)
            {
                throw new InvalidOperationException(L10n.Get("tools.pptEdit.slideNotFound", inp.SlideIndex, slideCount));
            }

            slide = pres.Slides[inp.SlideIndex.Value];
        }
        else
        {
            slide = app.ActiveWindow.View.Slide;
        }

        var left = (float)(inp.Left ?? 50);
        var top = (float)(inp.Top ?? 120);
        var width = (float)(inp.Width ?? 620);
        var height = (float)(inp.Height ?? Math.Max(30, rows * 28));

        dynamic shape = slide.Shapes.AddTable(rows, cols, left, top, width, height);
        dynamic table = shape.Table;

        if (cells is not null)
        {
            for (var r = 0; r < cells.Count && r < rows; r++)
            {
                var row = cells[r];
                for (var c = 0; c < row.Count && c < cols; c++)
                {
                    if (!string.IsNullOrEmpty(row[c]))
                    {
                        TrySet(() => table.Cell(r + 1, c + 1).Shape.TextFrame.TextRange.Text = row[c]);
                    }
                }
            }

            // 첫 행(헤더) 굵게 + 선택적 색상(배경·글자).
            var hFill = string.IsNullOrWhiteSpace(inp.HeaderFill) ? (int?)null : OfficeColor.ToBgr(inp.HeaderFill);
            var hColor = string.IsNullOrWhiteSpace(inp.HeaderColor) ? (int?)null : OfficeColor.ToBgr(inp.HeaderColor);
            for (var c = 1; c <= cols; c++)
            {
                var col = c;
                TrySet(() => table.Cell(1, col).Shape.TextFrame.TextRange.Font.Bold = MsoTrue);
                if (hFill is int f)
                {
                    TrySet(() =>
                    {
                        table.Cell(1, col).Shape.Fill.Solid();
                        table.Cell(1, col).Shape.Fill.ForeColor.RGB = f;
                    });
                }

                if (hColor is int fc)
                {
                    TrySet(() => table.Cell(1, col).Shape.TextFrame.TextRange.Font.Color.RGB = fc);
                }
            }
        }

        // 셀 폰트 크기: font_size 지정 시 전체 셀에 적용(PPT 표 기본 폰트가 커서 넘치는 것 방지).
        if (inp.FontSize is > 0)
        {
            var fs = (float)inp.FontSize.Value;
            for (var r = 1; r <= rows; r++)
            {
                for (var c = 1; c <= cols; c++)
                {
                    TrySet(() => table.Cell(r, c).Shape.TextFrame.TextRange.Font.Size = fs);
                }
            }
        }

        // 표 테두리선 색: 전체 셀의 4변에 적용(값범위 오류 방지 위해 셀·변별 best-effort).
        if (!string.IsNullOrWhiteSpace(inp.BorderColor))
        {
            var lc = OfficeColor.ToBgr(inp.BorderColor);
            for (var r = 1; r <= rows; r++)
            {
                for (var c = 1; c <= cols; c++)
                {
                    var rr = r; var cc = c;
                    foreach (var bi in PpCellBorders)
                    {
                        var b = bi;
                        TrySet(() => table.Cell(rr, cc).Borders[b].ForeColor.RGB = lc);
                    }
                }
            }
        }

        var where = inp.SlideIndex is not null ? L10n.Get("tools.pptEdit.whereSlide", inp.SlideIndex) : L10n.Get("tools.pptEdit.whereCurrentSlide");
        var filledSuffix = cells is not null ? L10n.Get("tools.pptEdit.tableFilledSuffix") : string.Empty;
        return L10n.Get("tools.pptEdit.tableInserted", where, rows, cols, filledSuffix);
    }

    // 기존 표 도형의 셀 하나를 수정. 대상 표는 slide_index+shape_id/현재 선택으로 지정.
    private static string SetCell(dynamic app, dynamic pres, Input inp)
    {
        var targets = ResolveTargets(app, pres, inp);
        dynamic? tableShape = null;
        foreach (var s in targets)
        {
            if (TryInt(() => (int)s.HasTable) == MsoTrue)
            {
                tableShape = s;
                break;
            }
        }

        if (tableShape is null)
        {
            throw new InvalidOperationException(L10n.Get("tools.pptEdit.tableShapeNotFound"));
        }

        dynamic targetCell = tableShape.Table.Cell(inp.Row!.Value, inp.Col!.Value);
        dynamic cellRange = targetCell.Shape.TextFrame.TextRange;
        cellRange.Text = inp.Text ?? string.Empty;
        if (inp.FontSize is > 0)
        {
            TrySet(() => cellRange.Font.Size = (float)inp.FontSize.Value);
        }

        if (!string.IsNullOrWhiteSpace(inp.Color))
        {
            var fc = OfficeColor.ToBgr(inp.Color);
            TrySet(() => cellRange.Font.Color.RGB = fc);
        }

        if (!string.IsNullOrWhiteSpace(inp.FillColor))
        {
            var bg = OfficeColor.ToBgr(inp.FillColor);
            TrySet(() =>
            {
                targetCell.Shape.Fill.Solid();
                targetCell.Shape.Fill.ForeColor.RGB = bg;
            });
        }

        if (!string.IsNullOrWhiteSpace(inp.BorderColor))
        {
            var lc = OfficeColor.ToBgr(inp.BorderColor);
            foreach (var bi in PpCellBorders)
            {
                var b = bi;
                TrySet(() => targetCell.Borders[b].ForeColor.RGB = lc);
            }
        }

        return L10n.Get("tools.pptEdit.cellUpdated", inp.Row, inp.Col);
    }

    private static string InsertPicture(dynamic app, dynamic pres, Input inp, string workingDir)
    {
        var file = OfficePicture.ResolvePath(inp.Path, workingDir);

        dynamic slide;
        if (inp.SlideIndex is not null)
        {
            int slideCount = (int)pres.Slides.Count;
            if (inp.SlideIndex.Value < 1 || inp.SlideIndex.Value > slideCount)
            {
                throw new InvalidOperationException(L10n.Get("tools.pptEdit.slideNotFound", inp.SlideIndex, slideCount));
            }

            slide = pres.Slides[inp.SlideIndex.Value];
        }
        else
        {
            slide = app.ActiveWindow.View.Slide; // 현재 편집 중인 슬라이드
        }

        var left = (float)(inp.Left ?? 100);
        var top = (float)(inp.Top ?? 100);
        var width = inp.Width is not null ? (float)inp.Width.Value : OfficePicture.KeepNative;
        var height = inp.Height is not null ? (float)inp.Height.Value : OfficePicture.KeepNative;

        slide.Shapes.AddPicture(
            file, OfficePicture.LinkToFileFalse, OfficePicture.SaveWithDocTrue, left, top, width, height);

        var where = inp.SlideIndex is not null ? L10n.Get("tools.pptEdit.whereSlide", inp.SlideIndex) : L10n.Get("tools.pptEdit.whereCurrentSlide");
        return L10n.Get("tools.pptEdit.pictureInserted", where);
    }

    // 대상 도형 목록을 만든다: 지정(slide_index+shape) 하나, 또는 현재 선택 전체.
    // PpSlideLayout: title(1) · title+content(2) · title only(11) · blank(12).
    private const int PpLayoutTitle = 1;
    private const int PpLayoutText = 2;
    private const int PpLayoutTitleOnly = 11;
    private const int PpLayoutBlank = 12;

    // 새 슬라이드를 추가하고(레이아웃), 제목/불릿을 바로 채운다 — 빈 프레젠테이션에서 초안 생성용.
    private static string AddSlide(dynamic pres, Input inp)
    {
        int count = (int)pres.Slides.Count;
        int index = inp.SlideIndex is int si && si >= 1 && si <= count + 1 ? si : count + 1;
        var layout = SlideLayoutId(inp.Layout);
        dynamic slide = pres.Slides.Add(index, layout);

        if (!string.IsNullOrWhiteSpace(inp.Text))
        {
            TrySet(() => slide.Shapes.Title.TextFrame.TextRange.Text = inp.Text);
        }

        if (inp.Bullets is { Count: > 0 })
        {
            var body = string.Join("\r", inp.Bullets.Where(b => !string.IsNullOrEmpty(b)));
            // 본문 플레이스홀더(보통 2번). 없으면 조용히 넘어간다(레이아웃에 따라 부재 가능).
            TrySet(() => slide.Shapes.Placeholders[2].TextFrame.TextRange.Text = body);
        }

        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(inp.Text)) { extras.Add(L10n.Get("tools.pptEdit.extraTitle")); }
        if (inp.Bullets is { Count: > 0 }) { extras.Add(L10n.Get("tools.pptEdit.extraBullets", inp.Bullets.Count)); }
        var filled = extras.Count > 0 ? L10n.Get("tools.pptEdit.slideAddedExtras", string.Join(", ", extras)) : string.Empty;
        return L10n.Get("tools.pptEdit.slideAdded", index, inp.Layout ?? "title_content", filled);
    }

    // 모든 슬라이드의 텍스트 도형을 순회하며 문자열 치환(도형 단위 read-modify-write). 치환된 도형 수 반환.
    private static string Replace(dynamic pres, string find, string replace)
    {
        var shapes = 0;
        int slideCount = (int)pres.Slides.Count;
        for (var si = 1; si <= slideCount; si++)
        {
            dynamic slide = pres.Slides[si];
            int shapeCount = (int)slide.Shapes.Count;
            for (var i = 1; i <= shapeCount; i++)
            {
                dynamic shape = slide.Shapes[i];
                if (TryInt(() => (int)shape.HasTextFrame) != MsoTrue)
                {
                    continue;
                }

                var text = TryStr(() => (string)shape.TextFrame.TextRange.Text);
                if (!string.IsNullOrEmpty(text) && text.Contains(find, StringComparison.Ordinal))
                {
                    if (TrySetOk(() => shape.TextFrame.TextRange.Text = text.Replace(find, replace, StringComparison.Ordinal)))
                    {
                        shapes++;
                    }
                }
            }
        }

        return L10n.Get("tools.pptEdit.replaceDone", shapes);
    }

    private static bool TrySetOk(Action set)
    {
        try { set(); return true; } catch { return false; }
    }

    private static int SlideLayoutId(string? layout) => layout?.Trim().ToLowerInvariant() switch
    {
        "blank" => PpLayoutBlank,
        "title_only" => PpLayoutTitleOnly,
        "title" => PpLayoutTitle,
        _ => PpLayoutText, // title_content
    };

    private static List<dynamic> ResolveTargets(dynamic app, dynamic pres, Input inp)
    {
        var list = new List<dynamic>();

        var hasShapeRef = inp.ShapeId is not null || !string.IsNullOrWhiteSpace(inp.ShapeName);
        if (hasShapeRef)
        {
            int slideCount = (int)pres.Slides.Count;
            if (inp.SlideIndex!.Value < 1 || inp.SlideIndex.Value > slideCount)
            {
                throw new InvalidOperationException(L10n.Get("tools.pptEdit.slideNotFound", inp.SlideIndex, slideCount));
            }

            dynamic slide = pres.Slides[inp.SlideIndex.Value];

            // scope 에 따라 검색할 Shapes 컬렉션 선택: 본문 / 레이아웃 배경 / 마스터 배경.
            dynamic shapesCol = (inp.Scope ?? "slide") switch
            {
                "layout" => slide.CustomLayout.Shapes,
                "master" => slide.CustomLayout.SlideMaster.Shapes,
                _ => slide.Shapes,
            };

            dynamic? shape = FindShape(shapesCol, inp.ShapeId, inp.ShapeName);
            if (shape is not null)
            {
                list.Add(shape);
            }

            return list;
        }

        // 대상 미지정 → 현재 선택한 도형들.
        dynamic sel = app.ActiveWindow.Selection;
        int selType = (int)sel.Type;
        if (selType is PpSelectionShapes or PpSelectionText)
        {
            dynamic shapeRange = sel.ShapeRange;
            int n = (int)shapeRange.Count;
            for (var i = 1; i <= n; i++)
            {
                list.Add(shapeRange[i]);
            }
        }

        return list;
    }

    private static void ApplyToShape(dynamic shape, Input inp)
    {
        switch (inp.Action)
        {
            case "set_text":
                // HasTextFrame 은 MsoTriState — (bool) 직접 캐스팅 금지.
                if ((int)shape.HasTextFrame == MsoFalse)
                {
                    throw new InvalidOperationException(L10n.Get("tools.pptEdit.noTextFrame", (string)shape.Name));
                }

                dynamic textRange = shape.TextFrame.TextRange;

                // 텍스트를 교체하면 (1) AutoFit 이 폰트를 극단 축소(예: 3pt)하거나 (2) 서식이 첫 문자
                // 기준으로 통일될 수 있다. 교체 전 대표 서식(크기·굵기·글꼴명)을 기억했다가 복원해 톤을 유지한다.
                float? keepSize = TryFloat(() => (float)textRange.Font.Size);
                int? keepBold = TryInt(() => (int)textRange.Font.Bold);
                string? keepName = TryStr(() => (string)textRange.Font.Name);

                textRange.Text = inp.Text;

                if (keepSize is > 0)
                {
                    TrySet(() => textRange.Font.Size = keepSize.Value);
                }

                if (keepBold is 0 or -1)
                {
                    TrySet(() => textRange.Font.Bold = keepBold.Value);
                }

                if (!string.IsNullOrEmpty(keepName))
                {
                    TrySet(() => textRange.Font.Name = keepName);
                }

                break;

            case "set_fill":
                shape.Fill.Visible = MsoTrue;
                shape.Fill.Solid();
                shape.Fill.ForeColor.RGB = OfficeColor.ToBgr(inp.Color!);
                break;

            case "set_font":
                if ((int)shape.HasTextFrame == MsoFalse)
                {
                    throw new InvalidOperationException(L10n.Get("tools.pptEdit.noTextFrame", (string)shape.Name));
                }

                dynamic font = shape.TextFrame.TextRange.Font;
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    font.Color.RGB = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.FontSize is not null)
                {
                    font.Size = inp.FontSize.Value;
                }

                if (inp.Bold is not null)
                {
                    font.Bold = inp.Bold.Value ? MsoTrue : MsoFalse;
                }

                break;

            case "set_line":
                shape.Line.Visible = MsoTrue;
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    shape.Line.ForeColor.RGB = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.LineWeight is not null)
                {
                    shape.Line.Weight = (float)inp.LineWeight.Value;
                }

                break;

            case "set_geometry":
                ShapeGeometry.Apply(shape, inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip);
                break;
        }
    }

    private static dynamic? FindShape(dynamic shapes, int? shapeId, string? name)
    {
        int count = (int)shapes.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic s = shapes[i];
            if (shapeId is not null && (int)s.Id == shapeId.Value)
            {
                return s;
            }

            if (shapeId is null && name is not null
                && string.Equals((string)s.Name, name, StringComparison.Ordinal))
            {
                return s;
            }
        }

        return null;
    }

    // COM 서식 값 캡처/설정 헬퍼(mixed·예외는 조용히 무시).
    private static float? TryFloat(Func<float> get)
    {
        try { return get(); } catch { return null; }
    }

    private static int? TryInt(Func<int> get)
    {
        try { return get(); } catch { return null; }
    }

    private static string? TryStr(Func<string> get)
    {
        try { return get(); } catch { return null; }
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { }
    }
}
