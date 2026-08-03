using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;

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

    private readonly StaDispatcher _sta;

    public ExcelEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "ExcelEdit";

    public string Description => """
        Edits the running Excel workbook (write). Actions:
          - set_value: set cell/range value (needs "value"; number-like strings become numbers)
          - set_formula: set a formula (needs "formula", e.g. "=SUM(A1:A10)")
          - set_font: color/size/bold (any of "color","font_size","bold")
          - set_fill: cell background color (needs "color")
          - insert_chart: chart from data (needs source via "cell"; "chart_type": column|line|pie|bar)
          - add_sheet: add a worksheet (optional "sheet_name")
          - set_geometry: move/resize/rotate/flip a shape on the active sheet (any of "left","top","width",
            "height" in points, "rotation" in degrees clockwise, "flip": horizontal|vertical). Target by
            1-based "shape_index" or "shape_name" (from ExcelInspect's shapes).
          - insert_picture: insert an image from a local file ("path") onto the active sheet (optional
            "left","top","width","height" in points; omit width/height for native size). Get the file first
            via ImageCreate (generated) or ImageFetch (from a web URL).
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
            "action": { "type": "string", "enum": ["set_value","set_formula","set_font","set_fill","insert_chart","add_sheet","set_geometry","insert_picture"] },
            "path": { "type": "string", "description": "Local image file path (insert_picture)" },
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
            "flip": { "type": "string", "description": "Flip the shape: horizontal | vertical (set_geometry)" }
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
        [property: JsonPropertyName("path")] string? Path);

    private static readonly string[] Actions =
        { "set_value", "set_formula", "set_font", "set_fill", "insert_chart", "add_sheet", "set_geometry", "insert_picture" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("ExcelEdit: Windows 전용 기능입니다.", IsError: true);
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
            yield return new ToolOutput($"ExcelEdit: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, System.StringComparer.Ordinal))
        {
            return "action 은 set_value|set_formula|set_font|set_fill|insert_chart|add_sheet 중 하나여야 합니다.";
        }

        return inp.Action switch
        {
            "set_value" when inp.Value is null => "set_value 에는 value 가 필요합니다.",
            "set_formula" when string.IsNullOrWhiteSpace(inp.Formula) => "set_formula 에는 formula 가 필요합니다.",
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => "set_font 에는 color, font_size, bold 중 하나가 필요합니다.",
            "set_fill" when string.IsNullOrWhiteSpace(inp.Color) => "set_fill 에는 color 가 필요합니다.",
            "set_geometry" when inp.ShapeIndex is null && string.IsNullOrWhiteSpace(inp.ShapeName)
                => "set_geometry 에는 shape_index 또는 shape_name 이 필요합니다.",
            "set_geometry" when ShapeGeometry.IsEmpty(inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip)
                => "set_geometry 에는 left, top, width, height, rotation, flip 중 하나가 필요합니다.",
            "set_geometry" => ShapeGeometry.ValidateFlip(inp.Flip),
            "insert_picture" when string.IsNullOrWhiteSpace(inp.Path) => "insert_picture 에는 path 가 필요합니다.",
            _ => null,
        };
    }

    private static string Apply(Input inp, string workingDir)
    {
        dynamic? app = ComInterop.TryGetActiveObject("Excel.Application");
        if (app is null)
        {
            throw new System.InvalidOperationException("Excel 이 실행 중이 아닙니다.");
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
            return "OK: 활성 시트에 이미지를 삽입했습니다.";
        }

        if (inp.Action == "add_sheet")
        {
            dynamic ws = wb.Worksheets.Add();
            if (!string.IsNullOrWhiteSpace(inp.SheetName))
            {
                ws.Name = inp.SheetName;
            }

            return $"OK: 시트를 추가했습니다{(string.IsNullOrWhiteSpace(inp.SheetName) ? string.Empty : $" ('{inp.SheetName}')")}.";
        }

        if (inp.Action == "insert_chart")
        {
            dynamic sheet = app.ActiveSheet;
            dynamic src = ResolveRange(app, inp.Cell);
            dynamic chartObj = sheet.ChartObjects().Add(300, 30, 360, 240);
            chartObj.Chart.SetSourceData(src);
            chartObj.Chart.ChartType = ChartTypeId(inp.ChartType);
            return "OK: 차트를 삽입했습니다.";
        }

        if (inp.Action == "set_geometry")
        {
            dynamic shape = ResolveShape(app, inp);
            var applied = ShapeGeometry.Apply(shape, inp.Left, inp.Top, inp.Width, inp.Height, inp.Rotation, inp.Flip);
            var target = inp.ShapeIndex is not null ? $"도형 {inp.ShapeIndex}" : $"도형 '{inp.ShapeName}'";
            return $"OK: {target} 에 기하 변경 {applied}건 적용.";
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
        }

        var scope = string.IsNullOrWhiteSpace(inp.Cell) ? "현재 선택" : inp.Cell;
        return $"OK: {scope} 에 {inp.Action} 적용.";
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
                    $"도형 {inp.ShapeIndex} 없음(활성 시트에 {count}개). ExcelInspect 의 shapes 를 확인하세요.");
            }

            return shapes.Item(inp.ShapeIndex.Value);
        }

        try
        {
            return shapes.Item(inp.ShapeName);
        }
        catch
        {
            throw new System.InvalidOperationException($"도형 '{inp.ShapeName}' 을 활성 시트에서 찾지 못했습니다.");
        }
    }

    private static int ChartTypeId(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "line" or "선" => XlLine,
        "pie" or "원" or "파이" => XlPie,
        "bar" or "가로막대" => XlBarClustered,
        _ => XlColumnClustered,
    };
}
