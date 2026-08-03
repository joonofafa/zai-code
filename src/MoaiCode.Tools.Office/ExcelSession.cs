using System.Globalization;
using System.Runtime.Versioning;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 실행 중인 Excel 에 late-binding 으로 연결해 활성 통합문서·워크시트·선택 범위를 조회한다.
/// 모든 COM 접근은 StaDispatcher 안에서 수행하고, 밖으로는 DTO 만 반환한다.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExcelSession
{
    private readonly StaDispatcher _sta;

    public ExcelSession(StaDispatcher sta) => _sta = sta;

    /// <summary>활성 통합문서 스냅샷. Excel 미실행/문서 없음이면 null.</summary>
    public Task<WorkbookInfo?> GetActiveWorkbookAsync(int maxSelectionRows = 50, int maxSelectionCols = 20) =>
        _sta.InvokeAsync(() => Inspect(maxSelectionRows, maxSelectionCols));

    private static WorkbookInfo? Inspect(int maxRows, int maxCols)
    {
        dynamic? app = ComInterop.TryGetActiveObject("Excel.Application");
        if (app is null)
        {
            return null; // Excel 미실행
        }

        dynamic? wb = TryGet(() => app.ActiveWorkbook);
        if (wb is null)
        {
            return null; // 열린 통합문서 없음
        }

        var sheets = new List<WorksheetInfo>();
        int sheetCount = (int)wb.Worksheets.Count;
        for (var i = 1; i <= sheetCount; i++)
        {
            dynamic ws = wb.Worksheets[i];
            dynamic? used = TryGet(() => ws.UsedRange);
            sheets.Add(new WorksheetInfo(
                Index: i,
                Name: (string)ws.Name,
                UsedRange: used is null ? null : TryGet(() => (string?)used.Address[false, false]),
                UsedRows: used is null ? 0 : TryGet(() => (int)used.Rows.Count),
                UsedColumns: used is null ? 0 : TryGet(() => (int)used.Columns.Count),
                TableCount: TryGet(() => (int)ws.ListObjects.Count),
                ChartCount: TryGet(() => (int)ws.ChartObjects().Count)));
        }

        return new WorkbookInfo(
            Name: (string)wb.Name,
            Path: TryGet(() => (string?)wb.FullName),
            ActiveSheet: TryGet(() => (string?)app.ActiveSheet.Name),
            Sheets: sheets,
            Selection: ReadSelection(app, maxRows, maxCols),
            Shapes: ReadShapes(app));
    }

    // 활성 시트의 도형 목록. 편집(set_geometry)은 활성 시트 도형을 대상으로 한다.
    private static IReadOnlyList<OfficeShapeInfo> ReadShapes(dynamic app)
    {
        var shapes = new List<OfficeShapeInfo>();
        dynamic? sheet = TryGet(() => app.ActiveSheet);
        if (sheet is null)
        {
            return shapes;
        }

        int count = TryGet(() => (int?)sheet.Shapes.Count) ?? 0;
        for (var i = 1; i <= count; i++)
        {
            dynamic? shape = TryGet(() => sheet.Shapes.Item(i));
            if (shape is null)
            {
                continue;
            }

            shapes.Add(new OfficeShapeInfo(
                Index: i,
                Name: TryGet(() => (string?)shape.Name) ?? $"Shape{i}",
                Left: TryGet(() => (double?)Convert.ToDouble(shape.Left)) ?? 0,
                Top: TryGet(() => (double?)Convert.ToDouble(shape.Top)) ?? 0,
                Width: TryGet(() => (double?)Convert.ToDouble(shape.Width)) ?? 0,
                Height: TryGet(() => (double?)Convert.ToDouble(shape.Height)) ?? 0,
                Rotation: TryGet(() => (double?)Convert.ToDouble(shape.Rotation)) ?? 0));
        }

        return shapes;
    }

    private static SelectionInfo? ReadSelection(dynamic app, int maxRows, int maxCols)
    {
        dynamic? sel = TryGet(() => app.Selection);
        if (sel is null)
        {
            return null;
        }

        var address = TryGet(() => (string?)sel.Address[false, false]) ?? "?";
        int rows = TryGet(() => (int)sel.Rows.Count);
        int cols = TryGet(() => (int)sel.Columns.Count);
        var take = Math.Min(rows, maxRows);
        var takeC = Math.Min(cols, maxCols);

        var grid = new List<IReadOnlyList<string?>>(take);
        for (var r = 1; r <= take; r++)
        {
            var row = new List<string?>(takeC);
            for (var c = 1; c <= takeC; c++)
            {
                // Text 는 셀에 표시되는 형식 문자열(사용자가 보는 값).
                row.Add(TryGet(() => Stringify(sel.Cells[r, c].Text)));
            }

            grid.Add(row);
        }

        return new SelectionInfo(address, rows, cols, Truncated: take < rows || takeC < cols, grid);
    }

    private static string? Stringify(object? v) =>
        v switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString(),
        };

    private static T? TryGet<T>(Func<T> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return default;
        }
    }
}
