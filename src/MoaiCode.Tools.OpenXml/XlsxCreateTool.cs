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
        Creates a new Excel workbook (.xlsx) with one or more sheets of rows, using the built-in
        Open XML writer — no dependencies, no Excel install. Cell values that parse as numbers are
        written as numbers; others as text. Supports native charts (bar/line/pie) and a bold header
        row — so charts do NOT need Python/openpyxl. ALWAYS use this to produce an .xlsx file. Do NOT
        install packages (openpyxl, exceljs, etc.) or write scripts to build spreadsheets.
        Charts reference vertical single-column ranges of the same sheet (e.g. categories "A2:A11",
        series values "B2:B11").
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
                  "rows": { "type": "array", "items": { "type": "array", "items": { "type": "string" } } },
                  "boldHeader": { "type": "boolean", "description": "Bold the first row (header). Default false." },
                  "charts": {
                    "type": "array",
                    "description": "Charts on this sheet, referencing its cell ranges",
                    "items": {
                      "type": "object",
                      "properties": {
                        "type": { "type": "string", "enum": ["bar", "line", "pie"], "description": "Chart type" },
                        "title": { "type": "string" },
                        "categories": { "type": "string", "description": "Vertical range for labels, e.g. A2:A11" },
                        "series": {
                          "type": "array",
                          "items": {
                            "type": "object",
                            "properties": {
                              "values": { "type": "string", "description": "Vertical value range, e.g. B2:B11" },
                              "name": { "type": "string", "description": "Series name (literal)" },
                              "nameRef": { "type": "string", "description": "Cell holding the series name, e.g. B1" }
                            },
                            "required": ["values"]
                          }
                        },
                        "anchor": { "type": "string", "description": "Top-left cell for the chart, e.g. H2" }
                      },
                      "required": ["categories", "series"]
                    }
                  }
                }
              }
            }
          },
          "required": ["path", "sheets"]
        }
        """).RootElement.Clone();

    private sealed record ChartSeriesIn(
        [property: JsonPropertyName("values")] string? Values,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("nameRef")] string? NameRef);

    private sealed record ChartIn(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("categories")] string? Categories,
        [property: JsonPropertyName("series")] List<ChartSeriesIn>? Series,
        [property: JsonPropertyName("anchor")] string? Anchor);

    private sealed record SheetIn(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("rows")] List<List<string>>? Rows,
        [property: JsonPropertyName("boldHeader")] bool? BoldHeader,
        [property: JsonPropertyName("charts")] List<ChartIn>? Charts);

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

        // 스타일시트(인덱스 1 = 굵게). 헤더 굵게용.
        var stylesPart = wbPart.AddNewPart<WorkbookStylesPart>();
        stylesPart.Stylesheet = BuildStylesheet();

        var sheetsEl = wbPart.Workbook.AppendChild(new Sheets());

        uint sheetId = 1;
        foreach (var sheet in sheets)
        {
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            wsPart.Worksheet = new Worksheet(data);

            var bold = sheet.BoldHeader == true;
            uint r = 1;
            foreach (var rowCells in sheet.Rows ?? new List<List<string>>())
            {
                var row = new Row { RowIndex = r };
                var col = 0;
                foreach (var value in rowCells)
                {
                    row.AppendChild(MakeCell(Reference(col, r), value, bold && r == 1 ? 1u : 0u));
                    col++;
                }

                data.AppendChild(row);
                r++;
            }

            // 차트(SheetData 뒤에 drawing 추가).
            var specs = MapCharts(sheet.Charts);
            if (specs.Count > 0)
            {
                var rows = (IReadOnlyList<IReadOnlyList<string>>)(sheet.Rows ?? new List<List<string>>())
                    .Select(x => (IReadOnlyList<string>)x).ToList();
                var sheetName = string.IsNullOrWhiteSpace(sheet.Name) ? $"Sheet{sheetId}" : sheet.Name!;
                XlsxChartBuilder.AddCharts(wsPart, sheetName, rows, specs);
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

    private static List<XlsxChartBuilder.ChartSpec> MapCharts(List<ChartIn>? charts)
    {
        var result = new List<XlsxChartBuilder.ChartSpec>();
        foreach (var c in charts ?? new List<ChartIn>())
        {
            if (string.IsNullOrWhiteSpace(c.Categories) || c.Series is null || c.Series.Count == 0)
            {
                continue; // 불완전한 차트 스펙은 건너뜀
            }

            var series = c.Series
                .Where(s => !string.IsNullOrWhiteSpace(s.Values))
                .Select(s => new XlsxChartBuilder.ChartSeries(s.Values!, s.Name, s.NameRef))
                .ToList();
            if (series.Count == 0)
            {
                continue;
            }

            result.Add(new XlsxChartBuilder.ChartSpec(
                string.IsNullOrWhiteSpace(c.Type) ? "bar" : c.Type!, c.Title, c.Categories!, series, c.Anchor));
        }

        return result;
    }

    private static Stylesheet BuildStylesheet()
    {
        return new Stylesheet(
            new Fonts(
                new Font(),                                   // 0: 기본
                new Font(new Bold())),                        // 1: 굵게
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None })),
            new Borders(new Border()),
            new CellFormats(
                new CellFormat(),                             // 0: 기본
                new CellFormat { FontId = 1, ApplyFont = true }));  // 1: 굵게
    }

    private static Cell MakeCell(string reference, string? value, uint styleIndex)
    {
        // 숫자로 파싱되면 숫자 셀(InvariantCulture — InvariantGlobalization 대응), 아니면 inline 문자열.
        if (!string.IsNullOrEmpty(value)
            && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            var c = new Cell
            {
                CellReference = reference,
                DataType = CellValues.Number,
                CellValue = new CellValue(num.ToString(CultureInfo.InvariantCulture)),
            };
            if (styleIndex != 0)
            {
                c.StyleIndex = styleIndex;
            }

            return c;
        }

        var cell = new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value ?? string.Empty)),
        };
        if (styleIndex != 0)
        {
            cell.StyleIndex = styleIndex;
        }

        return cell;
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
