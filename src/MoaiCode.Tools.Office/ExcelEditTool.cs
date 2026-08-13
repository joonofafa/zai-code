using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 활성 Excel 통합문서를 편집한다(쓰기). 액션:
///  set_value(셀/범위 값), set_formula(수식), set_font(색·크기·굵기),
///  set_fill(채우기 색), insert_chart(차트 삽입), add_sheet(시트 추가).
/// 대상: cell("A1"|"A1:B2") 지정, 없으면 현재 선택. Windows 전용.
/// </summary>
public sealed class ExcelEditTool : ITool
{
    // XlChartType.
    private const int XlColumnClustered = 51;
    private const int XlLine = 4;
    private const int XlPie = 5;
    private const int XlBarClustered = 57;
    private const int XlContinuous = 1;   // XlLineStyle.xlContinuous
    private const int XlThin = 2;         // XlBorderWeight.xlThin
    private const int XlMedium = -4138;   // XlBorderWeight.xlMedium
    private const int XlThick = 4;        // XlBorderWeight.xlThick
    private const int XlEdgeBottom = 9;   // XlBordersIndex.xlEdgeBottom
    private const int XlTypePDF = 0;    // XlFixedFormatType.xlTypePDF
    private const int XlPart = 2;       // XlLookAt.xlPart
    private const int XlCenter = -4108; // XlHAlign/XlVAlign.xlCenter

    private readonly StaDispatcher _sta;

    public ExcelEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "ExcelEdit";

    public string Description => """
        Edits the running Excel workbook (write). Actions:
          - set_value: set cell/range value (needs "value"; number-like strings become numbers)
          - set_formula: set a formula (needs "formula", e.g. "=SUM(A1:A10)")
          - set_font: color/size/bold (any of "color","font_size","bold")
          - set_fill: cell background color (needs "color")
          - set_border: draw borders on the "cell" range (thin continuous by default). "color" = line color,
            "border_weight" = thin|medium|thick. Applies to all edges + inside gridlines of the range.
          - set_shape_fill: fill (background) color of a shape on the active sheet (needs "color"). Target by
            "shape_index"/"shape_name".
          - set_shape_line: outline color/weight of a shape (any of "color", "line_weight" in points).
          - insert_divider: draw a horizontal divider = the BOTTOM border of the "cell" range/row (e.g. "A3:E3").
            "color" = line color, "border_weight" = thin|medium|thick.
          - insert_chart: chart from data (needs source via "cell"; "chart_type": column|line|pie|bar)
          - add_sheet: add a worksheet (optional "sheet_name")
          - set_geometry: move/resize/rotate/flip a shape on the active sheet (any of "left","top","width",
            "height" in points, "rotation" in degrees clockwise, "flip": horizontal|vertical). Target by
            1-based "shape_index" or "shape_name" (from ExcelInspect's shapes).
          - insert_picture: insert an image from a local file ("path") onto the active sheet (optional
            "left","top","width","height" in points; omit width/height for native size). Get the file first
            via ImageCreate (generated) or ImageFetch (from a web URL).
          - insert_pivot: create a PivotTable on a NEW sheet from a source range. "source" (e.g. "A1:D100",
            include the header row — field names come from it), "source_sheet" optional (default active).
            Place fields via "rows"/"columns"/"filters" (arrays of field names) and "values"
            (array of {field, func}; func: sum|count|average|max|min, default sum). Excel computes it.
            e.g. source "A1:D200", rows ["Department"], columns ["Month"], values [{"field":"Sales","func":"sum"}].
          - replace: find & replace ALL occurrences of "find_text" with "replace_text". Scoped to "cell"
            range if given, else the whole active sheet.
          - merge_cells: merge the "cell" range into one cell (centered). e.g. cell "A1:E1" for a title row.
          - delete_sheet: delete a worksheet ("sheet_name"; omit = active sheet). Cannot delete the last one.
          - delete_row: delete the entire row(s) of "cell" (e.g. "3" or "3:5" or "A3").
          - delete_column: delete the entire column(s) of "cell" (e.g. "B" or "B:C" or "B2").
          - delete_shape: delete a shape on the active sheet ("shape_index"/"shape_name").
          - export_pdf: export the workbook to PDF ("path" = output .pdf; if omitted, next to the workbook).
          - new_workbook: start a brand-new blank Excel workbook (launches Excel if not running) so you can
            then fill it with set_value/set_formula/etc. Use this when no workbook is open.
        Target the range by "cell" ("A1" or "A1:B2"); if omitted, the CURRENT SELECTION.
        Colors are "#RRGGBB" hex or a basic name. Windows only.
        """;

    public bool IsReadOnly => false;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set_value","set_formula","set_font","set_fill","set_border","insert_chart","add_sheet","set_geometry","insert_picture","insert_pivot","replace","merge_cells","delete_sheet","delete_row","delete_column","delete_shape","export_pdf","new_workbook","set_shape_fill","set_shape_line","insert_divider"] },
            "border_weight": { "type": "string", "description": "set_border line weight: thin|medium|thick (default thin)" },
            "line_weight": { "type": "number", "description": "Shape outline weight in points (set_shape_line)" },
            "path": { "type": "string", "description": "Local image file path (insert_picture) OR output .pdf path (export_pdf)" },
            "find_text": { "type": "string", "description": "Text to find (replace)" },
            "replace_text": { "type": "string", "description": "Replacement text (replace)" },
            "cell": { "type": "string", "description": "A1 or A1:B2 (omit to target current selection)" },
            "value": { "type": "string", "description": "value for set_value" },
            "formula": { "type": "string", "description": "formula for set_formula" },
            "color": { "type": "string", "description": "#RRGGBB or basic color name" },
            "font_size": { "type": "number" },
            "bold": { "type": "boolean" },
            "chart_type": { "type": "string", "description": "column|line|pie|bar" },
            "sheet_name": { "type": "string" },
            "shape_index": { "type": "integer", "description": "1-based shape index on active sheet (set_geometry)" },
            "shape_name": { "type": "string", "description": "Shape name (set_geometry, fallback for shape_index)" },
            "left": { "type": "number", "description": "X position in points (set_geometry)" },
            "top": { "type": "number", "description": "Y position in points (set_geometry)" },
            "width": { "type": "number", "description": "Width in points (set_geometry)" },
            "height": { "type": "number", "description": "Height in points (set_geometry)" },
            "rotation": { "type": "number", "description": "Rotation angle in degrees, clockwise (set_geometry)" },
            "flip": { "type": "string", "description": "Flip the shape: horizontal | vertical (set_geometry)" },
            "source": { "type": "string", "description": "insert_pivot: source data range, e.g. \"A1:D100\" (include the header row; field names come from it)" },
            "source_sheet": { "type": "string", "description": "insert_pivot: sheet name of the source range (omit = active sheet)" },
            "rows": { "type": "array", "items": { "type": "string" }, "description": "insert_pivot: field names placed on ROWS (e.g. [\"Department\"])" },
            "columns": { "type": "array", "items": { "type": "string" }, "description": "insert_pivot: field names placed on COLUMNS (e.g. [\"Month\"])" },
            "filters": { "type": "array", "items": { "type": "string" }, "description": "insert_pivot: field names used as page/report FILTERS" },
            "values": {
              "type": "array",
              "description": "insert_pivot: value fields with aggregation. Each {field, func}.",
              "items": {
                "type": "object",
                "properties": {
                  "field": { "type": "string", "description": "Field name to aggregate (e.g. \"Sales\")" },
                  "func": { "type": "string", "enum": ["sum","count","average","max","min","countnums"], "description": "Aggregation (default sum)" }
                },
                "required": ["field"]
              }
            }
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("cell")] string? Cell,
        [property: JsonPropertyName("value")] string? Value,
        [property: JsonPropertyName("formula")] string? Formula,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("font_size")] double? FontSize,
        [property: JsonPropertyName("bold")] bool? Bold,
        [property: JsonPropertyName("chart_type")] string? ChartType,
        [property: JsonPropertyName("sheet_name")] string? SheetName,
        [property: JsonPropertyName("shape_index")] int? ShapeIndex,
        [property: JsonPropertyName("shape_name")] string? ShapeName,
        [property: JsonPropertyName("left")] double? Left,
        [property: JsonPropertyName("top")] double? Top,
        [property: JsonPropertyName("width")] double? Width,
        [property: JsonPropertyName("height")] double? Height,
        [property: JsonPropertyName("rotation")] double? Rotation,
        [property: JsonPropertyName("flip")] string? Flip,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("source_sheet")] string? SourceSheet,
        [property: JsonPropertyName("rows")] List<string>? Rows,
        [property: JsonPropertyName("columns")] List<string>? Columns,
        [property: JsonPropertyName("filters")] List<string>? Filters,
        [property: JsonPropertyName("values")] List<PivotValueIn>? Values,
        [property: JsonPropertyName("find_text")] string? FindText,
        [property: JsonPropertyName("replace_text")] string? ReplaceText,
        [property: JsonPropertyName("border_weight")] string? BorderWeight,
        [property: JsonPropertyName("line_weight")] double? LineWeight);

    private sealed record PivotValueIn(
        [property: JsonPropertyName("field")] string? Field,
        [property: JsonPropertyName("func")] string? Func);

    private static readonly string[] Actions =
        { "set_value", "set_formula", "set_font", "set_fill", "set_border", "insert_chart", "add_sheet", "set_geometry", "insert_picture", "insert_pivot", "replace", "merge_cells", "delete_sheet", "delete_row", "delete_column", "delete_shape", "export_pdf", "new_workbook", "set_shape_fill", "set_shape_line", "insert_divider" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput(L10n.Get("tools.excelEdit.windowsOnly"), IsError: true);
            yield break;
        }

        var inp = input.Deserialize<Input>();
        var validationError = Validate(inp);
        if (validationError is not null)
        {
            yield return new ToolOutput($"ExcelEdit: {validationError}", IsError: true);
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
            MoaiLog.Error($"ExcelEdit: action={inp?.Action} threw", ex);
        }

        if (error is not null)
        {
            yield return new ToolOutput(L10n.Get("tools.excelEdit.failed", error), IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, System.StringComparer.Ordinal))
        {
            return L10n.Get("tools.excelEdit.invalidAction");
        }

        return inp.Action switch
        {
            "set_value" when inp.Value is null => L10n.Get("tools.excelEdit.setValueNeedsValue"),
            "set_formula" when string.IsNullOrWhiteSpace(inp.Formula) => L10n.Get("tools.excelEdit.setFormulaNeedsFormula"),
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => L10n.Get("tools.excelEdit.setFontNeedsAny"),
            "set_fill" when string.IsNullOrWhiteSpace(inp.Color) => L10n.Get("tools.excelEdit.setFillNeedsColor"),
            "set_geometry" when inp.ShapeIndex is null && string.IsNullOrWhiteSpace(inp.ShapeName)
                => L10n.Get("tools.excelEdit.setGeometryNeedsShape"),
            "set_shape_fill" when string.IsNullOrWhiteSpace(inp.Color) => L10n.Get("tools.excelEdit.setShapeFillNeedsColor"),
            "set_shape_line" when string.IsNullOrWhiteSpace(inp.Color) && inp.LineWeight is null
                => L10n.Get("tools.excelEdit.setShapeLineNeedsAny"),
            "set_geometry" when ShapeGeometry.IsEmpty(inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip)
                => L10n.Get("tools.excelEdit.setGeometryNeedsAny"),
            "set_geometry" => ShapeGeometry.ValidateFlip(inp.Flip),
            "insert_picture" when string.IsNullOrWhiteSpace(inp.Path) => L10n.Get("tools.excelEdit.insertPictureNeedsPath"),
            "insert_pivot" when string.IsNullOrWhiteSpace(inp.Source) => L10n.Get("tools.excelEdit.insertPivotNeedsSource"),
            "insert_pivot" when inp.Values is not { Count: > 0 } => L10n.Get("tools.excelEdit.insertPivotNeedsValues"),
            "replace" when string.IsNullOrEmpty(inp.FindText) => L10n.Get("tools.excelEdit.replaceNeedsFindText"),
            "merge_cells" when string.IsNullOrWhiteSpace(inp.Cell) => L10n.Get("tools.excelEdit.mergeCellsNeedsCell"),
            "delete_row" when string.IsNullOrWhiteSpace(inp.Cell) => L10n.Get("tools.excelEdit.deleteRowNeedsCell"),
            "delete_column" when string.IsNullOrWhiteSpace(inp.Cell) => L10n.Get("tools.excelEdit.deleteColumnNeedsCell"),
            "delete_shape" when inp.ShapeIndex is null && string.IsNullOrWhiteSpace(inp.ShapeName)
                => L10n.Get("tools.excelEdit.deleteShapeNeedsShape"),
            _ => null,
        };
    }

    private static string Apply(Input inp, string workingDir)
    {
        // new_workbook: 실행 중 Excel 에 붙거나, 없으면 새로 띄워 빈 통합문서를 만든다(작성 시작점).
        if (inp.Action == "new_workbook")
        {
            dynamic? xapp = ComInterop.GetOrCreate("Excel.Application");
            if (xapp is null)
            {
                throw new System.InvalidOperationException(L10n.Get("tools.excelEdit.cannotStart"));
            }

            xapp.Visible = true;
            xapp.Workbooks.Add();
            return L10n.Get("tools.excelEdit.newWorkbookOk");
        }

        dynamic? app = ComInterop.TryGetActiveObject("Excel.Application");
        if (app is null)
        {
            throw new System.InvalidOperationException(L10n.Get("tools.excelEdit.notRunning"));
        }

        dynamic wb = app.ActiveWorkbook; // 없으면 COMException

        if (inp.Action == "insert_picture")
        {
            var file = OfficePicture.ResolvePath(inp.Path, workingDir);
            var w = inp.Width is not null ? (float)inp.Width.Value : OfficePicture.KeepNative;
            var h = inp.Height is not null ? (float)inp.Height.Value : OfficePicture.KeepNative;
            app.ActiveSheet.Shapes.AddPicture(
                file, OfficePicture.LinkToFileFalse, OfficePicture.SaveWithDocTrue,
                (float)(inp.Left ?? 0), (float)(inp.Top ?? 0), w, h);
            return L10n.Get("tools.excelEdit.pictureInserted");
        }

        if (inp.Action == "add_sheet")
        {
            dynamic ws = wb.Worksheets.Add();
            if (!string.IsNullOrWhiteSpace(inp.SheetName))
            {
                ws.Name = inp.SheetName;
            }

            var sheetSuffix = string.IsNullOrWhiteSpace(inp.SheetName) ? string.Empty : $" ('{inp.SheetName}')";
            return L10n.Get("tools.excelEdit.sheetAdded", sheetSuffix);
        }

        if (inp.Action == "insert_chart")
        {
            dynamic sheet = app.ActiveSheet;
            dynamic src = ResolveRange(app, inp.Cell);
            dynamic chartObj = sheet.ChartObjects().Add(300, 30, 360, 240);
            chartObj.Chart.SetSourceData(src);
            chartObj.Chart.ChartType = ChartTypeId(inp.ChartType);
            return L10n.Get("tools.excelEdit.chartInserted");
        }

        if (inp.Action == "set_geometry")
        {
            dynamic shape = ResolveShape(app, inp);
            var applied = ShapeGeometry.Apply(shape, inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip);
            var target = inp.ShapeIndex is not null ? L10n.Get("tools.excelEdit.shapeByIndex", inp.ShapeIndex) : L10n.Get("tools.excelEdit.shapeByName", inp.ShapeName);
            return L10n.Get("tools.excelEdit.geometryApplied", target, applied);
        }

        if (inp.Action == "insert_pivot")
        {
            return InsertPivot(app, wb, inp);
        }

        if (inp.Action == "export_pdf")
        {
            var outPath = OfficePdf.Resolve(inp.Path, TryFullName(wb), workingDir);
            wb.ExportAsFixedFormat(XlTypePDF, outPath);
            return L10n.Get("tools.excelEdit.pdfExported", outPath);
        }

        if (inp.Action == "replace")
        {
            // cell 지정 시 그 범위, 없으면 활성 시트 전체 셀.
            dynamic scopeRange = string.IsNullOrWhiteSpace(inp.Cell)
                ? app.ActiveSheet.Cells
                : app.ActiveSheet.Range(inp.Cell);
            // Replace(What, Replacement, LookAt, ...)
            scopeRange.Replace(inp.FindText, inp.ReplaceText ?? string.Empty, XlPart);
            return L10n.Get("tools.excelEdit.replaceDone");
        }

        if (inp.Action == "merge_cells")
        {
            dynamic rng = app.ActiveSheet.Range(inp.Cell);
            rng.Merge();
            rng.HorizontalAlignment = XlCenter;
            rng.VerticalAlignment = XlCenter;
            return L10n.Get("tools.excelEdit.merged", inp.Cell);
        }

        if (inp.Action == "delete_sheet")
        {
            if ((int)wb.Worksheets.Count <= 1)
            {
                throw new System.InvalidOperationException(L10n.Get("tools.excelEdit.cannotDeleteLastSheet"));
            }

            dynamic ws = string.IsNullOrWhiteSpace(inp.SheetName) ? app.ActiveSheet : wb.Worksheets[inp.SheetName];
            var name = (string)ws.Name;
            var prevAlerts = app.DisplayAlerts;
            app.DisplayAlerts = false; // 삭제 확인 대화상자 억제
            try { ws.Delete(); }
            finally { app.DisplayAlerts = prevAlerts; }
            return L10n.Get("tools.excelEdit.sheetDeleted", name);
        }

        if (inp.Action == "delete_row")
        {
            app.ActiveSheet.Range(inp.Cell).EntireRow.Delete();
            return L10n.Get("tools.excelEdit.rowDeleted", inp.Cell);
        }

        if (inp.Action == "delete_column")
        {
            app.ActiveSheet.Range(inp.Cell).EntireColumn.Delete();
            return L10n.Get("tools.excelEdit.columnDeleted", inp.Cell);
        }

        if (inp.Action == "delete_shape")
        {
            dynamic shape = ResolveShape(app, inp);
            shape.Delete();
            return L10n.Get("tools.excelEdit.shapeDeleted");
        }

        if (inp.Action == "set_shape_fill")
        {
            dynamic shape = ResolveShape(app, inp);
            shape.Fill.Solid();
            shape.Fill.ForeColor.RGB = OfficeColor.ToBgr(inp.Color!);
            return L10n.Get("tools.excelEdit.shapeFillApplied");
        }

        if (inp.Action == "set_shape_line")
        {
            dynamic shape = ResolveShape(app, inp);
            if (!string.IsNullOrWhiteSpace(inp.Color))
            {
                shape.Line.ForeColor.RGB = OfficeColor.ToBgr(inp.Color);
            }

            if (inp.LineWeight is not null)
            {
                shape.Line.Weight = (float)inp.LineWeight.Value;
            }

            return L10n.Get("tools.excelEdit.shapeLineApplied");
        }

        if (inp.Action == "insert_divider")
        {
            dynamic dr = ResolveRange(app, inp.Cell);
            dynamic edge = dr.Borders[XlEdgeBottom];
            edge.LineStyle = XlContinuous;
            edge.Weight = BorderWeightId(inp.BorderWeight);
            if (!string.IsNullOrWhiteSpace(inp.Color))
            {
                edge.Color = OfficeColor.ToBgr(inp.Color);
            }

            var dscope = string.IsNullOrWhiteSpace(inp.Cell) ? L10n.Get("tools.excelEdit.currentSelection") : inp.Cell;
            return L10n.Get("tools.excelEdit.dividerAdded", dscope);
        }

        dynamic range = ResolveRange(app, inp.Cell);
        switch (inp.Action)
        {
            case "set_value":
                if (double.TryParse(inp.Value, out var num))
                {
                    range.Value2 = num;
                }
                else
                {
                    range.Value2 = inp.Value;
                }

                break;

            case "set_formula":
                range.Formula = inp.Formula;
                break;

            case "set_font":
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    range.Font.Color = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.FontSize is not null)
                {
                    range.Font.Size = inp.FontSize.Value;
                }

                if (inp.Bold is not null)
                {
                    range.Font.Bold = inp.Bold.Value;
                }

                break;

            case "set_fill":
                range.Interior.Color = OfficeColor.ToBgr(inp.Color!);
                break;

            case "set_border":
                dynamic borders = range.Borders;
                borders.LineStyle = XlContinuous;
                borders.Weight = BorderWeightId(inp.BorderWeight);
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    borders.Color = OfficeColor.ToBgr(inp.Color);
                }

                break;
        }

        var scope = string.IsNullOrWhiteSpace(inp.Cell) ? L10n.Get("tools.excelEdit.currentSelection") : inp.Cell;
        return L10n.Get("tools.excelEdit.actionApplied", scope, inp.Action);
    }

    // XlPivotFieldOrientation / XlConsolidationFunction / XlPivotTableSourceType 상수(late-binding int).
    private const int XlDatabase = 1;
    private const int XlRowField = 1;
    private const int XlColumnField = 2;
    private const int XlPageField = 3;
    private const int XlSum = -4157;
    private const int XlCount = -4112;
    private const int XlAverage = -4106;
    private const int XlMax = -4136;
    private const int XlMin = -4139;
    private const int XlCountNums = -4113;

    // Excel 피봇 엔진으로 새 시트에 피봇테이블을 만든다. source 헤더행에서 필드명을 얻는다.
    private static string InsertPivot(dynamic app, dynamic wb, Input inp)
    {
        // 소스 범위(지정 시트 또는 활성 시트).
        dynamic srcSheet = string.IsNullOrWhiteSpace(inp.SourceSheet)
            ? app.ActiveSheet
            : wb.Worksheets[inp.SourceSheet];
        dynamic srcRange = srcSheet.Range(inp.Source);

        // 대상: 새 시트(피봇은 보통 별도 시트).
        dynamic dest = wb.Worksheets.Add();
        var destName = UniqueSheetName(wb, "피벗");
        try { dest.Name = destName; } catch { /* 이름 충돌 등 — 기본 이름 유지 */ }

        // 캐시 → 피봇테이블(A1 배치).
        dynamic cache = wb.PivotCaches().Create(XlDatabase, srcRange);
        dynamic pt = cache.CreatePivotTable(dest.Range("A1"), "PivotTable_MoAI");

        // 필드 배치. 없는 필드명은 COM 예외 → 흡수하고 계속(부분 성공).
        var placed = 0;
        foreach (var f in inp.Rows ?? new List<string>())
        {
            if (SetOrientation(pt, f, XlRowField)) { placed++; }
        }

        foreach (var f in inp.Columns ?? new List<string>())
        {
            if (SetOrientation(pt, f, XlColumnField)) { placed++; }
        }

        foreach (var f in inp.Filters ?? new List<string>())
        {
            if (SetOrientation(pt, f, XlPageField)) { placed++; }
        }

        var vals = 0;
        foreach (var v in inp.Values ?? new List<PivotValueIn>())
        {
            if (string.IsNullOrWhiteSpace(v.Field))
            {
                continue;
            }

            try
            {
                pt.AddDataField(pt.PivotFields(v.Field), System.Type.Missing, PivotFuncId(v.Func));
                vals++;
            }
            catch (System.Exception ex)
            {
                MoaiLog.Debug($"ExcelPivot: value field '{v.Field}' skip: {ex.GetType().Name}");
            }
        }

        if (vals == 0)
        {
            throw new System.InvalidOperationException(L10n.Get("tools.excelEdit.pivotNoValueField"));
        }

        return L10n.Get("tools.excelEdit.pivotCreated", destName, placed, vals);
    }

    // 필드 방향 지정. 없는 필드명이면 COM 예외 → false(흡수).
    private static bool SetOrientation(dynamic pt, string? field, int orientation)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return false;
        }

        try
        {
            pt.PivotFields(field).Orientation = orientation;
            return true;
        }
        catch (System.Exception ex)
        {
            MoaiLog.Debug($"ExcelPivot: field '{field}' orientation skip: {ex.GetType().Name}");
            return false;
        }
    }

    private static int PivotFuncId(string? func) => func?.Trim().ToLowerInvariant() switch
    {
        "count" => XlCount,
        "average" or "avg" => XlAverage,
        "max" => XlMax,
        "min" => XlMin,
        "countnums" => XlCountNums,
        _ => XlSum,
    };

    // wb 안에서 base/base2/base3… 중 겹치지 않는 시트명.
    private static string UniqueSheetName(dynamic wb, string baseName)
    {
        var existing = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (dynamic ws in wb.Worksheets)
        {
            existing.Add((string)ws.Name);
        }

        if (!existing.Contains(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 100; i++)
        {
            var name = baseName + i;
            if (!existing.Contains(name))
            {
                return name;
            }
        }

        return baseName + System.Guid.NewGuid().ToString("N")[..4];
    }

    // 저장 안 된 통합문서는 FullName 이 이름만 오거나 던질 수 있으므로 안전하게.
    private static string? TryFullName(dynamic wb)
    {
        try { return (string)wb.FullName; } catch { return null; }
    }

    // cell 지정 시 활성 시트의 그 범위, 없으면 현재 선택.
    private static dynamic ResolveRange(dynamic app, string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell))
        {
            return app.Selection;
        }

        return app.ActiveSheet.Range(cell);
    }

    // 활성 시트에서 대상 도형을 찾는다: shape_index 우선, 없으면 shape_name.
    private static dynamic ResolveShape(dynamic app, Input inp)
    {
        dynamic shapes = app.ActiveSheet.Shapes;
        if (inp.ShapeIndex is not null)
        {
            int count = (int)shapes.Count;
            if (inp.ShapeIndex.Value < 1 || inp.ShapeIndex.Value > count)
            {
                throw new System.InvalidOperationException(
                    L10n.Get("tools.excelEdit.shapeNotFoundByIndex", inp.ShapeIndex, count));
            }

            return shapes.Item(inp.ShapeIndex.Value);
        }

        try
        {
            return shapes.Item(inp.ShapeName);
        }
        catch
        {
            throw new System.InvalidOperationException(L10n.Get("tools.excelEdit.shapeNotFoundByName", inp.ShapeName));
        }
    }

    private static int BorderWeightId(string? weight) => weight?.Trim().ToLowerInvariant() switch
    {
        "medium" or "중간" => XlMedium,
        "thick" or "굵게" or "두껍게" => XlThick,
        _ => XlThin,
    };

    private static int ChartTypeId(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "line" or "선" => XlLine,
        "pie" or "원" or "파이" => XlPie,
        "bar" or "가로막대" => XlBarClustered,
        _ => XlColumnClustered,
    };
}
