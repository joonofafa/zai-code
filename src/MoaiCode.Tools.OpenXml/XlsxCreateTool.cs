using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.OpenXml;

/// <summary>새 Excel 통합문서(.xlsx)를 만든다. Open XML SDK — Excel 설치 불필요, 전 플랫폼.</summary>
public sealed class XlsxCreateTool : ITool
{
    public string Name => "XlsxCreate";

    public string Description => """
        Creates a new Excel workbook (.xlsx) with one or more sheets of rows. Cell values that
        parse as numbers are written as numbers; others as text. No Excel install needed (Open XML).
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Output .xlsx path (relative to workspace)" },
            "sheets": {
              "type": "array",
              "description": "Sheets; each a name + rows (array of string cells)",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } }
                }
              }
            }
          },
          "required": ["path", "sheets"]
        }
        """).RootElement.Clone();

    private sealed record SheetIn(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("rows")] List<List<string>>? Rows);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("sheets")] List<SheetIn>? Sheets);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path) || inp.Sheets is null || inp.Sheets.Count == 0)
        {
            yield return new ToolOutput("XlsxCreate: 'path' 와 최소 1개 'sheets' 가 필요합니다.", IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        try
        {
            full = OpenXmlPaths.ResolveForWrite(context.WorkingDirectory, inp.Path, ".xlsx");
            Write(full, inp.Sheets);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        yield return error is not null
            ? new ToolOutput($"XlsxCreate: 실패 — {error}", IsError: true)
            : new ToolOutput($"OK: {full} 생성 ({inp.Sheets.Count} 시트).");
    }

    private static void Write(string path, List<SheetIn> sheets)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wbPart = doc.AddWorkbookPart();
        wbPart.Workbook = new Workbook();
        var sheetsEl = wbPart.Workbook.AppendChild(new Sheets());

        uint sheetId = 1;
        foreach (var sheet in sheets)
        {
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            wsPart.Worksheet = new Worksheet(data);

            uint r = 1;
            foreach (var rowCells in sheet.Rows ?? new List<List<string>>())
            {
                var row = new Row { RowIndex = r };
                var col = 0;
                foreach (var value in rowCells)
                {
                    row.AppendChild(MakeCell(Reference(col, r), value));
                    col++;
                }

                data.AppendChild(row);
                r++;
            }

            sheetsEl.AppendChild(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = sheetId,
                Name = string.IsNullOrWhiteSpace(sheet.Name) ? $"Sheet{sheetId}" : sheet.Name!,
            });
            sheetId++;
        }
    }

    private static Cell MakeCell(string reference, string? value)
    {
        // 숫자로 파싱되면 숫자 셀(InvariantCulture — InvariantGlobalization 대응), 아니면 inline 문자열.
        if (!string.IsNullOrEmpty(value)
            && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            return new Cell
            {
                CellReference = reference,
                DataType = CellValues.Number,
                CellValue = new CellValue(num.ToString(CultureInfo.InvariantCulture)),
            };
        }

        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty)),
        };
    }

    // 0-based col, 1-based row → "A1" 형식.
    private static string Reference(int col, uint row)
    {
        var name = string.Empty;
        var c = col;
        do
        {
            name = (char)('A' + (c % 26)) + name;
            c = c / 26 - 1;
        }
        while (c >= 0);

        return name + row.ToString(CultureInfo.InvariantCulture);
    }
}
