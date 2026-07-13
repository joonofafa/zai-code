using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 실행 중인 Excel 의 활성 통합문서·워크시트·선택 범위를 조회한다(읽기 전용).
/// 전체 시트를 무제한으로 읽지 않고 선택 범위·요약만 반환한다(원본 설계 §ExcelInspect).
/// </summary>
public sealed class ExcelInspectTool : ITool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StaDispatcher _sta;

    public ExcelInspectTool(StaDispatcher sta) => _sta = sta;

    public string Name => "ExcelInspect";

    public string Description => """
        Inspects the running Excel: active workbook, worksheets, and the current selection
        (address + displayed values, capped) (read-only). Use BEFORE editing to read current
        state. Windows only; requires Excel running with an open workbook.
        """;

    public bool IsReadOnly => true;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "max_rows": { "type": "integer", "description": "Max selection rows to return (default 50)" },
            "max_cols": { "type": "integer", "description": "Max selection columns to return (default 20)" }
          }
        }
        """).RootElement.Clone();

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("ExcelInspect: Windows 전용 기능입니다.", IsError: true);
            yield break;
        }

        var maxRows = ReadInt(input, "max_rows", 50, 1, 500);
        var maxCols = ReadInt(input, "max_cols", 20, 1, 100);

        WorkbookInfo? info = null;
        string? error = null;
        try
        {
            info = await new ExcelSession(_sta).GetActiveWorkbookAsync(maxRows, maxCols).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            yield return new ToolOutput($"ExcelInspect: 조회 실패 — {error}", IsError: true);
            yield break;
        }

        if (info is null)
        {
            yield return new ToolOutput("ExcelInspect: 실행 중인 Excel 에 열린 통합문서가 없습니다.");
            yield break;
        }

        yield return new ToolOutput(JsonSerializer.Serialize(info, JsonOpts));
    }

    private static int ReadInt(JsonElement input, string name, int def, int min, int max) =>
        input.ValueKind == JsonValueKind.Object
        && input.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number
            ? Math.Clamp(v.GetInt32(), min, max)
            : def;
}
