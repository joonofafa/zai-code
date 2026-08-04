using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;

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
            "정리/표로" real content, so it becomes a real Word table, not tab-separated text.
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
            "row", "col" (1-based), "text" = new cell content.
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
            "action": { "type": "string", "enum": ["set_text","set_font","set_style","insert_paragraph","delete_paragraph","insert_table","set_geometry","insert_picture","add_page","replace","export_pdf","delete_shape","new_document","set_cell"] },
            "table_index": { "type": "integer", "description": "1-based table index in the document (set_cell; default 1)" },
            "row": { "type": "integer", "description": "1-based cell row (set_cell)" },
            "col": { "type": "integer", "description": "1-based cell column (set_cell)" },
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
        { "set_text", "set_font", "set_style", "insert_paragraph", "delete_paragraph", "insert_table", "set_geometry", "insert_picture", "add_page", "replace", "export_pdf", "delete_shape", "new_document", "set_cell" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("WordEdit: Windows 전용 기능입니다.", IsError: true);
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
            yield return new ToolOutput($"WordEdit: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, System.StringComparer.Ordinal))
        {
            return "action 은 set_text|set_font|set_style|insert_paragraph|delete_paragraph|insert_table 중 하나여야 합니다.";
        }

        return inp.Action switch
        {
            "set_text" when inp.Text is null => "set_text 에는 text 가 필요합니다.",
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => "set_font 에는 color, font_size, bold 중 하나가 필요합니다.",
            "set_style" when string.IsNullOrWhiteSpace(inp.Style) => "set_style 에는 style 이 필요합니다.",
            "insert_paragraph" when inp.Text is null => "insert_paragraph 에는 text 가 필요합니다.",
            "delete_paragraph" when inp.ParaIndex is null => "delete_paragraph 에는 para_index 가 필요합니다.",
            "insert_table" when (inp.Rows is null or < 1 || inp.Cols is null or < 1)
                    && (inp.Cells is null || inp.Cells.Count == 0)
                => "insert_table 에는 rows·cols(1 이상) 또는 cells(내용)가 필요합니다.",
            "set_geometry" when ShapeGeometry.IsEmpty(inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip)
                => "set_geometry 에는 left, top, width, height, rotation, flip 중 하나가 필요합니다.",
            "set_geometry" => ShapeGeometry.ValidateFlip(inp.Flip),
            "insert_picture" when string.IsNullOrWhiteSpace(inp.Path) => "insert_picture 에는 path 가 필요합니다.",
            "replace" when string.IsNullOrEmpty(inp.FindText) => "replace 에는 find_text 가 필요합니다.",
            "set_cell" when inp.Row is null or < 1 || inp.Col is null or < 1 || inp.Text is null
                => "set_cell 에는 row·col(1 이상)·text 가 필요합니다.",
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
                throw new System.InvalidOperationException("Word 를 시작할 수 없습니다(설치 확인).");
            }

            wapp.Visible = true;
            wapp.Documents.Add();
            wapp.Activate();
            return "OK: 새 Word 문서를 열었습니다.";
        }

        dynamic? app = ComInterop.TryGetActiveObject("Word.Application");
        if (app is null)
        {
            throw new System.InvalidOperationException("Word 가 실행 중이 아닙니다.");
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
                    return "OK: 이미지를 삽입했습니다(떠있는 도형).";
                }

                dynamic inline = app.Selection.InlineShapes.AddPicture(
                    file, OfficePicture.LinkToFileFalse, OfficePicture.SaveWithDocTrue);
                if (inp.Width is not null) { inline.Width = (float)inp.Width.Value; }
                if (inp.Height is not null) { inline.Height = (float)inp.Height.Value; }
                return "OK: 이미지를 삽입했습니다(인라인).";
            }

            case "insert_paragraph":
            {
                dynamic content = doc.Content;
                content.InsertAfter("\r" + inp.Text);
                dynamic last = doc.Paragraphs[(int)doc.Paragraphs.Count].Range;

                // Word 는 새 문단이 '앞 문단' 서식을 상속한다. 앞이 제목(48pt)이면 본문도 제목이 되어버린다.
                // style 을 지정했으면 그 스타일, 없으면 본문(Normal)로 강제해 톤앤매너를 유지한다.
                last.Style = string.IsNullOrWhiteSpace(inp.Style) ? WdStyleNormal : StyleId(inp.Style!);
                return "OK: 문단을 추가했습니다.";
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

                return "OK: 새 페이지를 추가했습니다" + (string.IsNullOrWhiteSpace(inp.Text) ? "." : "(내용 포함).");
            }

            case "delete_paragraph":
            {
                int count = (int)doc.Paragraphs.Count;
                if (inp.ParaIndex!.Value < 1 || inp.ParaIndex.Value > count)
                {
                    throw new System.InvalidOperationException($"문단 {inp.ParaIndex} 없음(현재 {count}개).");
                }

                doc.Paragraphs[inp.ParaIndex.Value].Range.Delete();
                return $"OK: 문단 {inp.ParaIndex} 를 삭제했습니다.";
            }

            case "insert_table":
            {
                var cells = inp.Cells;
                int rows = inp.Rows ?? cells?.Count ?? 0;
                int cols = inp.Cols ?? (cells is { Count: > 0 } ? cells.Max(r => r.Count) : 0);
                if (rows < 1 || cols < 1)
                {
                    throw new System.InvalidOperationException("표 크기를 알 수 없습니다(rows/cols 또는 cells 필요).");
                }

                dynamic tRange = Target(app, doc, inp);
                dynamic table = doc.Tables.Add(tRange, rows, cols);
                try { table.Borders.Enable = 1; } catch { /* 스타일에 따라 실패 무시 */ }

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

                    // 첫 행(헤더) 굵게.
                    try
                    {
                        for (var c = 1; c <= cols; c++)
                        {
                            table.Cell(1, c).Range.Font.Bold = 1;
                        }
                    }
                    catch { /* 셀 병합 등으로 인덱스 어긋나면 무시 */ }
                }

                return $"OK: {rows}x{cols} 표를 삽입했습니다{(cells is not null ? " (내용 채움)" : string.Empty)}.";
            }

            case "set_geometry":
            {
                dynamic shape = ResolveShape(app, doc, inp);
                var applied = ShapeGeometry.Apply(shape, inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip);
                var target = inp.ShapeIndex is not null ? $"도형 {inp.ShapeIndex}"
                    : !string.IsNullOrWhiteSpace(inp.ShapeName) ? $"도형 '{inp.ShapeName}'" : "현재 선택 도형";
                return $"OK: {target} 에 기하 변경 {applied}건 적용.";
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
                return "OK: 찾기·바꾸기 완료.";
            }

            case "export_pdf":
            {
                var outPath = OfficePdf.Resolve(inp.Path, TryFullName(doc), workingDir);
                doc.ExportAsFixedFormat(outPath, WdExportFormatPDF);
                return $"OK: PDF 로 내보냈습니다 — {outPath}";
            }

            case "delete_shape":
            {
                dynamic shape = ResolveShape(app, doc, inp);
                shape.Delete();
                return "OK: 도형을 삭제했습니다.";
            }

            case "set_cell":
            {
                int ti = inp.TableIndex ?? 1;
                int tcount = (int)doc.Tables.Count;
                if (ti < 1 || ti > tcount)
                {
                    throw new System.InvalidOperationException($"표 {ti} 없음(현재 {tcount}개). WordInspect 로 확인하세요.");
                }

                // Cell.Range.Text 대입은 셀 내용을 교체한다(셀마커는 보존).
                doc.Tables[ti].Cell(inp.Row!.Value, inp.Col!.Value).Range.Text = inp.Text;
                return $"OK: 표 {ti} 의 ({inp.Row},{inp.Col}) 셀을 수정했습니다.";
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

        var scope = inp.ParaIndex is not null ? $"문단 {inp.ParaIndex}" : "현재 선택";
        return $"OK: {scope} 에 {inp.Action} 적용.";
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
                    $"문단 {inp.ParaIndex} 없음(현재 {count}개). 새 내용은 set_text 가 아니라 insert_paragraph 로 추가하고, " +
                    "편집 전 WordInspect 로 실제 문단 수를 확인하세요.");
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
                    $"도형 {inp.ShapeIndex} 없음(현재 {count}개). WordInspect 의 shapes 에서 인덱스를 확인하세요.");
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
                throw new System.InvalidOperationException($"도형 '{inp.ShapeName}' 을 찾지 못했습니다.");
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
            throw new System.InvalidOperationException(
                "대상 도형이 없습니다. shape_index/shape_name 으로 지정하거나, Word 에서 도형을 선택한 뒤 다시 시도하세요.");
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
