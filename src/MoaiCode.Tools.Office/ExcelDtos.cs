namespace MoaiCode.Tools.Office;

// Excel 조회 DTO. PowerPoint 와 동일 원칙 — COM 객체 대신 스냅샷만 STA 밖으로 넘긴다.

/// <summary>활성 통합문서 스냅샷.</summary>
public sealed record WorkbookInfo(
    string Name,
    string? Path,
    string? ActiveSheet,
    IReadOnlyList<WorksheetInfo> Sheets,
    SelectionInfo? Selection,
    // 활성 시트의 도형 목록. ExcelEdit set_geometry 의 대상 참조용(1-based Index/Name).
    IReadOnlyList<OfficeShapeInfo>? Shapes = null);

/// <summary>워크시트 스냅샷. UsedRange 는 데이터가 있는 범위(A1:D20 형식).</summary>
public sealed record WorksheetInfo(
    int Index,
    string Name,
    string? UsedRange,
    int UsedRows,
    int UsedColumns,
    int TableCount,
    int ChartCount);

/// <summary>현재 선택 범위. Values 는 표시값(캡 적용). 큰 선택은 잘라서 반환.</summary>
public sealed record SelectionInfo(
    string Address,
    int Rows,
    int Columns,
    bool Truncated,
    IReadOnlyList<IReadOnlyList<string?>> Values);
