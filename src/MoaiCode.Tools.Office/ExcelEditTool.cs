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
            "action": { "type": "string", "enum": ["set_value","set_formula","set_font","set_fill","insert_chart","add_sheet"] },
            "cell": { "type": "string", "description": "A1 or A1:B2 (omit to target current selection)" },
            "value": { "type": "string", "description": "value for set_value" },
            "formula": { "type": "string", "description": "formula for set_formula" },
            "color": { "type": "string", "description": "#RRGGBB or basic color name" },
            "font_size": { "type": "number" },
            "bold": { "type": "boolean" },
            "chart_type": { "type": "string", "description": "column|line|pie|bar" },
            "sheet_name": { "type": "string" }
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
        [property: JsonPropertyName("sheet_name")] string? SheetName);

    private static readonly string[] Actions =
        { "set_value", "set_formula", "set_font", "set_fill", "insert_chart", "add_sheet" };

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
            result = await _sta.InvokeAsync(() => Apply(inp!)).ConfigureAwait(false);
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
            _ => null,
        };
    }

    private static string Apply(Input inp)
    {
        dynamic? app = ComInterop.TryGetActiveObject("Excel.Application");
        if (app is null)
        {
            throw new System.InvalidOperationException("Excel 이 실행 중이 아닙니다.");
        }

        dynamic wb = app.ActiveWorkbook; // 없으면 COMException

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

    private static int ChartTypeId(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "line" or "선" => XlLine,
        "pie" or "원" or "파이" => XlPie,
        "bar" or "가로막대" => XlBarClustered,
        _ => XlColumnClustered,
    };
}
