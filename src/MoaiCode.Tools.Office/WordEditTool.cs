using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 활성 Word 문서를 편집한다(쓰기). 액션:
///  set_text(선택/문단 텍스트), set_font(색·크기·굵기), set_style(제목/본문 스타일),
///  insert_paragraph(문단 추가), delete_paragraph(문단 삭제), insert_table(표 삽입).
/// 대상: para_index 지정, 없으면 현재 선택. Windows 전용.
/// </summary>
public sealed class WordEditTool : ITool
{
    // WdBuiltinStyle.
    private const int WdPageBreak = 7;   // WdBreakType.wdPageBreak
    private const int WdCollapseEnd = 0;  // WdCollapseDirection.wdCollapseEnd
    private const int WdStyleNormal = -1;
    private const int WdStyleHeading1 = -2;
    private const int WdStyleHeading2 = -3;
    private const int WdStyleHeading3 = -4;
    private const int WdStyleTitle = -63;
    private const int WdReplaceAll = 2;      // WdReplace.wdReplaceAll
    private const int WdFindContinue = 1;    // WdFindWrap.wdFindContinue
    private const int WdExportFormatPDF = 17; // WdExportFormat.wdExportFormatPDF

    private readonly StaDispatcher _sta;

    public WordEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "WordEdit";

    public string Description => """
        Edits the running Word document (write). Actions:
          - set_text: replace target text (needs "text")
          - set_font: color/size/bold (any of "color","font_size","bold")
          - set_style: paragraph style ("style": heading1|heading2|heading3|title|normal)
          - insert_paragraph: append a new paragraph at end (needs "text"; optional "style").
            Body text defaults to Normal style (does NOT inherit the previous heading). Set "style" only for headings.
          - delete_paragraph: delete paragraph at "para_index"
          - insert_table: insert a table AND fill it. Provide "cells" (array of rows, each an array of cell
            strings) to create a bordered table with content — size is inferred and the first row is bolded as
            a header. (Or give "rows"/"cols" for an empty table.) Prefer "cells" whenever the user asks to
            organize real content into a table, so it becomes a real Word table, not tab-separated text.
            PLACEMENT: the table goes AFTER "para_index" (1-based, from WordInspect) — inspect first to find the
            RIGHT paragraph (e.g. the end of the "2. Basic Principles" section) instead of dropping it at the cursor.
            FONT: cell font defaults to the document body (Normal) size; pass "font_size" to override. This
            avoids the table inheriting a big heading font.
            HEADER COLOR: pass "header_fill" (header row background) and/or "header_color" (header text color)
            to theme the first row (e.g. brand color). For a single cell's color later, use set_cell.
            BORDER COLOR: pass "border_color" to color all table border lines.
          - set_geometry: move/resize/rotate/flip a floating shape (any of "left","top","width","height"
            in points, "rotation" in degrees clockwise, "flip": horizontal|vertical). Target the shape by
            1-based "shape_index" or "shape_name" (from WordInspect's shapes); if omitted, the CURRENT SELECTION.
          - insert_picture: insert an image from a local file ("path"). Inline at the current selection by
            default; if "left"/"top" are given it is placed as a floating shape. Optional "width"/"height"
            in points. Get the file first via ImageCreate (generated) or ImageFetch (from a web URL).
          - replace: find & replace ALL occurrences of "find_text" with "replace_text" across the document.
          - export_pdf: export the document to PDF ("path" = output .pdf; if omitted, next to the document).
          - delete_shape: delete a floating shape (by "shape_index"/"shape_name", or the current selection).
          - new_document: start a brand-new blank Word document (launches Word if it is not running) so you
            can then write into it with insert_paragraph/set_style/etc. Use this when no document is open.
          - set_cell: edit an existing table cell. "table_index" (1-based, from WordInspect; default 1),
            "row", "col" (1-based), "text" = new cell content. Optional "color" (cell text color),
            "fill_color" (cell background), "border_color" (cell border line color), "font_size".
          - set_shape_fill: fill (background) color of a floating shape (needs "color"). Target by
            "shape_index"/"shape_name" or the current selection.
          - set_shape_line: outline color/weight of a shape (any of "color", "line_weight" in points).
          - insert_divider: insert a real horizontal rule (a paragraph with a bottom border line), NOT literal
            "----" text. At "para_index" (after it) or the document end. Optional "border_color" for line color.
            Use this whenever the user asks for a divider / horizontal rule.
        Target the paragraph by 1-based "para_index" (from WordInspect); if omitted, the CURRENT SELECTION.
        IMPORTANT: set_text only edits EXISTING paragraphs (1..N as reported by WordInspect). Never use an
        out-of-range para_index — to ADD new content use insert_paragraph. Always WordInspect first to get N.
        Colors are "#RRGGBB" hex or a basic name. Windows only.
        """;

    public bool IsReadOnly => false;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set_text","set_font","set_style","insert_paragraph","delete_paragraph","insert_table","set_geometry","insert_picture","add_page","replace","export_pdf","delete_shape","new_document","set_cell","set_shape_fill","set_shape_line","insert_divider"] },
            "border_color": { "type": "string", "description": "Table/cell border line color, #RRGGBB or name (insert_table/set_cell)" },
            "line_weight": { "type": "number", "description": "Shape outline weight in points (set_shape_line)" },
            "table_index": { "type": "integer", "description": "1-based table index in the document (set_cell; default 1)" },
            "row": { "type": "integer", "description": "1-based cell row (set_cell)" },
            "col": { "type": "integer", "description": "1-based cell column (set_cell)" },
            "fill_color": { "type": "string", "description": "Cell background color, #RRGGBB or name (set_cell)" },
            "header_fill": { "type": "string", "description": "Header row (first row) background color (insert_table)" },
            "header_color": { "type": "string", "description": "Header row text color (insert_table)" },
            "path": { "type": "string", "description": "Local image file path (insert_picture) OR output .pdf path (export_pdf)" },
            "find_text": { "type": "string", "description": "Text to find (replace)" },
            "replace_text": { "type": "string", "description": "Replacement text (replace)" },
            "para_index": { "type": "integer", "description": "1-based paragraph index (omit to target current selection)" },
            "text": { "type": "string" },
            "color": { "type": "string", "description": "#RRGGBB or basic color name" },
            "font_size": { "type": "number" },
            "bold": { "type": "boolean" },
            "style": { "type": "string", "description": "heading1|heading2|heading3|title|normal" },
            "rows": { "type": "integer" },
            "cols": { "type": "integer" },
            "cells": { "type": "array", "items": { "type": "array", "items": { "type": "string" } }, "description": "insert_table content: rows of cell strings. Table size is inferred; first row is bolded as header." },
            "shape_index": { "type": "integer", "description": "1-based floating-shape index (set_geometry)" },
            "shape_name": { "type": "string", "description": "Shape name (set_geometry, fallback for shape_index)" },
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
        [property: JsonPropertyName("para_index")] int? ParaIndex,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("font_size")] double? FontSize,
        [property: JsonPropertyName("bold")] bool? Bold,
        [property: JsonPropertyName("style")] string? Style,
        [property: JsonPropertyName("rows")] int? Rows,
        [property: JsonPropertyName("cols")] int? Cols,
        [property: JsonPropertyName("cells")] List<List<string>>? Cells,
        [property: JsonPropertyName("table_index")] int? TableIndex,
        [property: JsonPropertyName("row")] int? Row,
        [property: JsonPropertyName("col")] int? Col,
        [property: JsonPropertyName("fill_color")] string? FillColor,
        [property: JsonPropertyName("header_fill")] string? HeaderFill,
        [property: JsonPropertyName("header_color")] string? HeaderColor,
        [property: JsonPropertyName("border_color")] string? BorderColor,
        [property: JsonPropertyName("line_weight")] double? LineWeight,
        [property: JsonPropertyName("shape_index")] int? ShapeIndex,
        [property: JsonPropertyName("shape_name")] string? ShapeName,
        [property: JsonPropertyName("left")] double? Left,
        [property: JsonPropertyName("top")] double? Top,
        [property: JsonPropertyName("width")] double? Width,
        [property: JsonPropertyName("height")] double? Height,
        [property: JsonPropertyName("rotation")] double? Rotation,
        [property: JsonPropertyName("flip")] string? Flip,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("find_text")] string? FindText,
        [property: JsonPropertyName("replace_text")] string? ReplaceText);

    private static readonly string[] Actions =
        { "set_text", "set_font", "set_style", "insert_paragraph", "delete_paragraph", "insert_table", "set_geometry", "insert_picture", "add_page", "replace", "export_pdf", "delete_shape", "new_document", "set_cell", "set_shape_fill", "set_shape_line", "insert_divider" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput(L10n.Get("tools.wordEdit.windowsOnly"), IsError: true);
            yield break;
        }

        var inp = input.Deserialize<Input>();
        var validationError = Validate(inp);
        if (validationError is not null)
        {
            yield return new ToolOutput($"WordEdit: {validationError}", IsError: true);
            yield break;
        }

        string result;
        string? error = null;
        try
        {
            result = await _sta.InvokeAsync(() => Apply(inp!, context.WorkingDirectory)).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
            MoaiLog.Error($"WordEdit: action={inp?.Action} threw", ex);
        }

        if (error is not null)
        {
            yield return new ToolOutput(L10n.Get("tools.wordEdit.failed", error), IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, System.StringComparer.Ordinal))
        {
            return L10n.Get("tools.wordEdit.invalidAction");
        }

        return inp.Action switch
        {
            "set_text" when inp.Text is null => L10n.Get("tools.wordEdit.setTextNeedsText"),
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => L10n.Get("tools.wordEdit.setFontNeedsAny"),
            "set_style" when string.IsNullOrWhiteSpace(inp.Style) => L10n.Get("tools.wordEdit.setStyleNeedsStyle"),
            "insert_paragraph" when inp.Text is null => L10n.Get("tools.wordEdit.insertParagraphNeedsText"),
            "delete_paragraph" when inp.ParaIndex is null => L10n.Get("tools.wordEdit.deleteParagraphNeedsIndex"),
            "insert_table" when (inp.Rows is null or < 1 || inp.Cols is null or < 1)
                    && (inp.Cells is null || inp.Cells.Count == 0)
                => L10n.Get("tools.wordEdit.insertTableNeedsSize"),
            "set_geometry" when ShapeGeometry.IsEmpty(inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip)
                => L10n.Get("tools.wordEdit.setGeometryNeedsAny"),
            "set_geometry" => ShapeGeometry.ValidateFlip(inp.Flip),
            "insert_picture" when string.IsNullOrWhiteSpace(inp.Path) => L10n.Get("tools.wordEdit.insertPictureNeedsPath"),
            "replace" when string.IsNullOrEmpty(inp.FindText) => L10n.Get("tools.wordEdit.replaceNeedsFindText"),
            "set_cell" when inp.Row is null or < 1 || inp.Col is null or < 1 || inp.Text is null
                => L10n.Get("tools.wordEdit.setCellNeedsRowColText"),
            "set_shape_fill" when string.IsNullOrWhiteSpace(inp.Color) => L10n.Get("tools.wordEdit.setShapeFillNeedsColor"),
            "set_shape_line" when string.IsNullOrWhiteSpace(inp.Color) && inp.LineWeight is null
                => L10n.Get("tools.wordEdit.setShapeLineNeedsAny"),
            _ => null,
        };
    }

    private static string Apply(Input inp, string workingDir)
    {
        // new_document: 실행 중 Word 에 붙거나, 없으면 새로 띄워 빈 문서를 만든다(작성 시작점).
        if (inp.Action == "new_document")
        {
            dynamic? wapp = ComInterop.GetOrCreate("Word.Application");
            if (wapp is null)
            {
                throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.cannotStart"));
            }

            wapp.Visible = true;
            wapp.Documents.Add();
            wapp.Activate();
            return L10n.Get("tools.wordEdit.newDocumentOk");
        }

        dynamic? app = ComInterop.TryGetActiveObject("Word.Application");
        if (app is null)
        {
            throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.notRunning"));
        }

        dynamic doc = app.ActiveDocument; // 없으면 COMException

        switch (inp.Action)
        {
            case "insert_picture":
            {
                var file = OfficePicture.ResolvePath(inp.Path, workingDir);

                // left/top 지정 시 떠있는 도형, 없으면 현재 선택 위치에 인라인 삽입.
                if (inp.Left is not null || inp.Top is not null)
                {
                    var w = inp.Width is not null ? (float)inp.Width.Value : OfficePicture.KeepNative;
                    var h = inp.Height is not null ? (float)inp.Height.Value : OfficePicture.KeepNative;
                    doc.Shapes.AddPicture(
                        file, OfficePicture.LinkToFileFalse, OfficePicture.SaveWithDocTrue,
                        (float)(inp.Left ?? 0), (float)(inp.Top ?? 0), w, h);
                    return L10n.Get("tools.wordEdit.pictureInsertedFloating");
                }

                dynamic inline = app.Selection.InlineShapes.AddPicture(
                    file, OfficePicture.LinkToFileFalse, OfficePicture.SaveWithDocTrue);
                if (inp.Width is not null) { inline.Width = (float)inp.Width.Value; }
                if (inp.Height is not null) { inline.Height = (float)inp.Height.Value; }
                return L10n.Get("tools.wordEdit.pictureInsertedInline");
            }

            case "insert_paragraph":
            {
                dynamic content = doc.Content;
                content.InsertAfter("\r" + inp.Text);
                dynamic last = doc.Paragraphs[(int)doc.Paragraphs.Count].Range;

                // Word 는 새 문단이 '앞 문단' 서식을 상속한다. 앞이 제목(48pt)이면 본문도 제목이 되어버린다.
                // style 을 지정했으면 그 스타일, 없으면 본문(Normal)로 강제해 톤앤매너를 유지한다.
                last.Style = string.IsNullOrWhiteSpace(inp.Style) ? WdStyleNormal : StyleId(inp.Style!);
                return L10n.Get("tools.wordEdit.paragraphAdded");
            }

            case "add_page":
            {
                // 문서 끝에 페이지 나눔을 넣어 새 페이지를 시작하고, text 가 있으면 그 페이지에 문단으로 채운다.
                dynamic rng = doc.Content;
                rng.Collapse(WdCollapseEnd);
                rng.InsertBreak(WdPageBreak);
                if (!string.IsNullOrWhiteSpace(inp.Text))
                {
                    rng.InsertAfter(inp.Text);
                    dynamic last = doc.Paragraphs[(int)doc.Paragraphs.Count].Range;
                    last.Style = string.IsNullOrWhiteSpace(inp.Style) ? WdStyleNormal : StyleId(inp.Style!);
                }

                return string.IsNullOrWhiteSpace(inp.Text)
                    ? L10n.Get("tools.wordEdit.pageAdded")
                    : L10n.Get("tools.wordEdit.pageAddedWithContent");
            }

            case "delete_paragraph":
            {
                int count = (int)doc.Paragraphs.Count;
                if (inp.ParaIndex!.Value < 1 || inp.ParaIndex.Value > count)
                {
                    throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.paragraphNotFound", inp.ParaIndex, count));
                }

                doc.Paragraphs[inp.ParaIndex.Value].Range.Delete();
                return L10n.Get("tools.wordEdit.paragraphDeleted", inp.ParaIndex);
            }

            case "insert_table":
            {
                var cells = inp.Cells;
                int rows = inp.Rows ?? cells?.Count ?? 0;
                int cols = inp.Cols ?? (cells is { Count: > 0 } ? cells.Max(r => r.Count) : 0);
                if (rows < 1 || cols < 1)
                {
                    throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.tableSizeUnknown"));
                }

                dynamic tRange = Target(app, doc, inp);
                // para_index 로 위치 지정 시 그 문단 '뒤'에 삽입(문단 전체를 표로 흡수하지 않도록 끝으로 축소).
                if (inp.ParaIndex is not null)
                {
                    try { tRange.Collapse(WdCollapseEnd); } catch { /* 축소 실패 시 그대로 */ }
                }

                dynamic table = doc.Tables.Add(tRange, rows, cols);
                try { table.Borders.Enable = 1; } catch { /* 스타일에 따라 실패 무시 */ }

                if (!string.IsNullOrWhiteSpace(inp.BorderColor))
                {
                    var bc = OfficeColor.ToBgr(inp.BorderColor);
                    // WdBorderType: top(-1) left(-2) bottom(-3) right(-4) horizontal(-5) vertical(-6).
                    foreach (var bi in new[] { -1, -2, -3, -4, -5, -6 })
                    {
                        try { table.Borders[bi].LineStyle = 1; table.Borders[bi].Color = bc; } catch { }
                    }
                }

                // 셀 폰트: font_size 지정값, 없으면 문서 본문(Normal) 크기로 통일 — 삽입 위치의
                // 큰 폰트(제목 근처 등) 상속으로 표 글자가 커지는 것을 막는다.
                float tblSize = 0;
                if (inp.FontSize is > 0)
                {
                    tblSize = (float)inp.FontSize.Value;
                }
                else
                {
                    try { tblSize = (float)doc.Styles[WdStyleNormal].Font.Size; } catch { tblSize = 0; }
                }

                if (tblSize > 0)
                {
                    try { table.Range.Font.Size = tblSize; } catch { /* 무시 */ }
                }

                if (cells is not null)
                {
                    for (var r = 0; r < cells.Count && r < rows; r++)
                    {
                        var row = cells[r];
                        for (var c = 0; c < row.Count && c < cols; c++)
                        {
                            if (!string.IsNullOrEmpty(row[c]))
                            {
                                // 셀 Range.Text 는 끝에 셀마커를 포함하므로 값만 대입한다.
                                table.Cell(r + 1, c + 1).Range.Text = row[c];
                            }
                        }
                    }

                    // 첫 행(헤더) 굵게 + 선택적 색상(배경·글자).
                    var hFill = string.IsNullOrWhiteSpace(inp.HeaderFill) ? (int?)null : OfficeColor.ToBgr(inp.HeaderFill);
                    var hColor = string.IsNullOrWhiteSpace(inp.HeaderColor) ? (int?)null : OfficeColor.ToBgr(inp.HeaderColor);
                    for (var c = 1; c <= cols; c++)
                    {
                        dynamic hcell = table.Cell(1, c);
                        try { hcell.Range.Font.Bold = 1; } catch { }
                        if (hFill is int f) { try { hcell.Shading.BackgroundPatternColor = f; } catch { } }
                        if (hColor is int fc) { try { hcell.Range.Font.Color = fc; } catch { } }
                    }
                }

                var tblFilledSuffix = cells is not null ? L10n.Get("tools.wordEdit.tableFilledSuffix") : string.Empty;
                return L10n.Get("tools.wordEdit.tableInserted", rows, cols, tblFilledSuffix);
            }

            case "set_geometry":
            {
                dynamic shape = ResolveShape(app, doc, inp);
                var applied = ShapeGeometry.Apply(shape, inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip);
                var target = inp.ShapeIndex is not null ? L10n.Get("tools.wordEdit.shapeByIndex", inp.ShapeIndex)
                    : !string.IsNullOrWhiteSpace(inp.ShapeName) ? L10n.Get("tools.wordEdit.shapeByName", inp.ShapeName) : L10n.Get("tools.wordEdit.currentSelectionShape");
                return L10n.Get("tools.wordEdit.geometryApplied", target, applied);
            }

            case "replace":
            {
                dynamic find = doc.Content.Find;
                find.ClearFormatting();
                find.Replacement.ClearFormatting();
                // Execute(FindText, MatchCase, MatchWholeWord, MatchWildcards, MatchSoundsLike,
                //   MatchAllWordForms, Forward, Wrap, Format, ReplaceWith, Replace)
                find.Execute(inp.FindText, false, false, false, false, false, true,
                    WdFindContinue, false, inp.ReplaceText ?? string.Empty, WdReplaceAll);
                return L10n.Get("tools.wordEdit.replaceDone");
            }

            case "export_pdf":
            {
                var outPath = OfficePdf.Resolve(inp.Path, TryFullName(doc), workingDir);
                doc.ExportAsFixedFormat(outPath, WdExportFormatPDF);
                return L10n.Get("tools.wordEdit.pdfExported", outPath);
            }

            case "delete_shape":
            {
                dynamic shape = ResolveShape(app, doc, inp);
                shape.Delete();
                return L10n.Get("tools.wordEdit.shapeDeleted");
            }

            case "set_shape_fill":
            {
                dynamic shape = ResolveShape(app, doc, inp);
                shape.Fill.Solid();
                shape.Fill.ForeColor.RGB = OfficeColor.ToBgr(inp.Color!);
                return L10n.Get("tools.wordEdit.shapeFillApplied");
            }

            case "set_shape_line":
            {
                dynamic shape = ResolveShape(app, doc, inp);
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    shape.Line.ForeColor.RGB = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.LineWeight is not null)
                {
                    shape.Line.Weight = (float)inp.LineWeight.Value;
                }

                return L10n.Get("tools.wordEdit.shapeLineApplied");
            }

            case "insert_divider":
            {
                // 실제 가로줄 = 빈 문단 + 아래쪽 테두리(텍스트 '----' 가 아님).
                dynamic anchor = inp.ParaIndex is not null ? Target(app, doc, inp) : doc.Content;
                anchor.Collapse(WdCollapseEnd);
                anchor.InsertParagraphAfter();
                try
                {
                    dynamic b = anchor.Paragraphs[1].Range.Borders[-3]; // wdBorderBottom
                    b.LineStyle = 1;   // wdLineStyleSingle
                    b.LineWidth = 6;   // wdLineWidth075pt
                    if (!string.IsNullOrWhiteSpace(inp.BorderColor))
                    {
                        b.Color = OfficeColor.ToBgr(inp.BorderColor);
                    }
                }
                catch { /* 테두리 적용 실패 시에도 문단은 추가됨 */ }

                return L10n.Get("tools.wordEdit.dividerAdded");
            }

            case "set_cell":
            {
                int ti = inp.TableIndex ?? 1;
                int tcount = (int)doc.Tables.Count;
                if (ti < 1 || ti > tcount)
                {
                    throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.tableNotFound", ti, tcount));
                }

                // Cell.Range.Text 대입은 셀 내용을 교체한다(셀마커는 보존).
                dynamic cell = doc.Tables[ti].Cell(inp.Row!.Value, inp.Col!.Value);
                cell.Range.Text = inp.Text;
                if (inp.FontSize is > 0)
                {
                    try { cell.Range.Font.Size = (float)inp.FontSize.Value; } catch { /* 무시 */ }
                }

                if (!string.IsNullOrWhiteSpace(inp.FillColor))
                {
                    try { cell.Shading.BackgroundPatternColor = OfficeColor.ToBgr(inp.FillColor); } catch { /* 무시 */ }
                }

                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    try { cell.Range.Font.Color = OfficeColor.ToBgr(inp.Color); } catch { /* 무시 */ }
                }

                if (!string.IsNullOrWhiteSpace(inp.BorderColor))
                {
                    var bc = OfficeColor.ToBgr(inp.BorderColor);
                    foreach (var bi in new[] { -1, -2, -3, -4 }) // 셀 4변(top/left/bottom/right)
                    {
                        try { cell.Borders[bi].LineStyle = 1; cell.Borders[bi].Color = bc; } catch { }
                    }
                }

                return L10n.Get("tools.wordEdit.cellUpdated", ti, inp.Row, inp.Col);
            }
        }

        // set_text / set_font / set_style — 대상 Range 에 적용.
        dynamic range = Target(app, doc, inp);
        switch (inp.Action)
        {
            case "set_text":
                // 문단 Range 는 끝에 ¶(문단기호)를 포함한다. 그대로 Text 를 설정하면
                // '줄 끝에 대한 작업이 잘못되었습니다'(0x800A1483)가 난다. 문단 대상이면 ¶ 를 제외한다.
                if (inp.ParaIndex is not null)
                {
                    try
                    {
                        range.MoveEnd(1 /* wdCharacter */, -1);
                    }
                    catch
                    {
                        // 축소 실패 시 그대로 시도.
                    }
                }

                range.Text = inp.Text;
                break;

            case "set_font":
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    range.Font.Color = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.FontSize is not null)
                {
                    range.Font.Size = (float)inp.FontSize.Value;
                }

                if (inp.Bold is not null)
                {
                    range.Font.Bold = inp.Bold.Value ? 1 : 0;
                }

                break;

            case "set_style":
                range.Style = StyleId(inp.Style!);
                break;
        }

        var scope = inp.ParaIndex is not null ? L10n.Get("tools.wordEdit.scopeParagraph", inp.ParaIndex) : L10n.Get("tools.wordEdit.currentSelection");
        return L10n.Get("tools.wordEdit.actionApplied", scope, inp.Action);
    }

    // 대상 Range: para_index 지정 시 해당 문단, 없으면 현재 선택.
    private static dynamic Target(dynamic app, dynamic doc, Input inp)
    {
        if (inp.ParaIndex is not null)
        {
            int count = (int)doc.Paragraphs.Count;
            if (inp.ParaIndex.Value < 1 || inp.ParaIndex.Value > count)
            {
                throw new System.InvalidOperationException(
                    L10n.Get("tools.wordEdit.paragraphNotFoundHint", inp.ParaIndex, count));
            }

            return doc.Paragraphs[inp.ParaIndex.Value].Range;
        }

        return app.Selection.Range;
    }

    // 대상 도형: shape_index/shape_name 지정 시 그 떠있는 도형, 없으면 현재 선택한 도형.
    private static dynamic ResolveShape(dynamic app, dynamic doc, Input inp)
    {
        if (inp.ShapeIndex is not null)
        {
            int count = (int)doc.Shapes.Count;
            if (inp.ShapeIndex.Value < 1 || inp.ShapeIndex.Value > count)
            {
                throw new System.InvalidOperationException(
                    L10n.Get("tools.wordEdit.shapeNotFoundByIndex", inp.ShapeIndex, count));
            }

            return doc.Shapes[inp.ShapeIndex.Value];
        }

        if (!string.IsNullOrWhiteSpace(inp.ShapeName))
        {
            try
            {
                return doc.Shapes[inp.ShapeName];
            }
            catch
            {
                throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.shapeNotFoundByName", inp.ShapeName));
            }
        }

        // 미지정 → 현재 선택한 도형(ShapeRange). 도형이 선택돼 있지 않으면 실패.
        dynamic? shape = null;
        try
        {
            shape = app.Selection.ShapeRange[1];
        }
        catch
        {
            // 무시하고 아래에서 에러 처리.
        }

        if (shape is null)
        {
            throw new System.InvalidOperationException(L10n.Get("tools.wordEdit.noTargetShape"));
        }

        return shape;
    }

    // 저장 안 된 문서는 FullName 이 이름만("Document1") 오거나 던질 수 있으므로 안전하게.
    private static string? TryFullName(dynamic doc)
    {
        try { return (string)doc.FullName; } catch { return null; }
    }

    private static int StyleId(string style) => style.Trim().ToLowerInvariant() switch
    {
        "heading1" or "h1" or "제목1" or "제목 1" => WdStyleHeading1,
        "heading2" or "h2" or "제목2" or "제목 2" => WdStyleHeading2,
        "heading3" or "h3" or "제목3" or "제목 3" => WdStyleHeading3,
        "title" or "제목" => WdStyleTitle,
        "normal" or "본문" or "표준" => WdStyleNormal,
        _ => WdStyleNormal,
    };
}
