using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 기존 Excel 통합문서(.xlsx)의 셀 값을 좌표로 채운다(템플릿 채우기). 값은 자동 타이핑되며
/// (숫자/수식/텍스트), 수식 인젝션 가드와 모던함수 _xlfn. 정규화는 XlsxCreate 와 공유한다.
/// 셀 서식(통화·날짜 등)은 원본 템플릿의 것을 그대로 유지한다(값만 갱신).
/// </summary>
public sealed class XlsxEditTool : ITool
{
    public string Name => "XlsxEdit";

    public string Description => """
        Edits an EXISTING .xlsx by setting cell values at given references (template filling). Provide
        "path" and "cells" (each: cell like "B2", value, optional sheet name — defaults to the first
        sheet). Values are auto-typed like XlsxCreate: a plain number → number; a string starting with
        "=" → live formula (modern functions auto-prefixed; injection payloads neutralized to text);
        anything else → text. The cell's existing number format (currency/date/etc.) from the template
        is preserved — only the value changes. Use this to fill a company/report .xlsx template instead
        of recreating it. For a NEW workbook, use XlsxCreate.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Existing .xlsx path (relative to workspace)" },
            "cells": {
              "type": "array",
              "description": "Cell value assignments.",
              "items": {
                "type": "object",
                "properties": {
                  "sheet": { "type": "string", "description": "Sheet name. Omit for the first sheet." },
                  "cell": { "type": "string", "description": "Cell reference, e.g. 'B2'" },
                  "value": { "type": "string", "description": "New value (auto-typed: number / '=formula' / text)" }
                },
                "required": ["cell", "value"]
              }
            }
          },
          "required": ["path", "cells"]
        }
        """).RootElement.Clone();

    private sealed record CellEdit(
        [property: JsonPropertyName("sheet")] string? Sheet,
        [property: JsonPropertyName("cell")] string? Cell,
        [property: JsonPropertyName("value")] string? Value);

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("cells")] List<CellEdit>? Cells);

    private static readonly Regex RefRe = new(@"^([A-Za-z]{1,3})([1-9][0-9]*)$", RegexOptions.Compiled);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        var edits = (inp?.Cells ?? new List<CellEdit>())
            .Where(e => !string.IsNullOrWhiteSpace(e.Cell))
            .ToList();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path) || edits.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.xlsxEdit.inputRequired"), IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        var count = 0;
        try
        {
            full = OpenXmlPaths.ResolveForRead(context.WorkingDirectory, inp.Path);
            if (!File.Exists(full))
            {
                error = L10n.Get("tools.xlsxEdit.notFound", inp.Path);
            }
            else
            {
                count = Apply(full, edits);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        yield return error is not null
            ? new ToolOutput(L10n.Get("tools.xlsxEdit.failed", error), IsError: true)
            : new ToolOutput(L10n.Get("tools.xlsxEdit.ok", full, count));
    }

    private static int Apply(string path, List<CellEdit> edits)
    {
        // 원본 보호: 임시 복사본에서 수정한 뒤 원자적 교체 — 수정/저장 중 실패해도 원본이 깨지지 않는다.
        var tmp = path + ".tmp";
        File.Copy(path, tmp, overwrite: true);
        var count = 0;
        try
        {
            using (var doc = SpreadsheetDocument.Open(tmp, isEditable: true))
            {
                var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("no workbook part");
                var sheets = wbPart.Workbook.Descendants<Sheet>().ToList();
                if (sheets.Count == 0)
                {
                    throw new InvalidOperationException("workbook has no sheets");
                }

                foreach (var e in edits)
                {
                    var m = RefRe.Match(e.Cell!.Trim());
                    if (!m.Success)
                    {
                        continue; // 잘못된 참조는 건너뛴다.
                    }

                    var cellRef = m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value;
                    var rowIdx = uint.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

                    var target = string.IsNullOrWhiteSpace(e.Sheet)
                        ? sheets[0]
                        : sheets.FirstOrDefault(s => string.Equals(s.Name?.Value, e.Sheet, StringComparison.OrdinalIgnoreCase));
                    if (target?.Id?.Value is not { } relId || wbPart.GetPartById(relId) is not WorksheetPart wsPart)
                    {
                        continue;
                    }

                    var cell = GetOrCreateCell(wsPart.Worksheet, cellRef, rowIdx);
                    SetValue(cell, e.Value ?? string.Empty);
                    count++;
                }

                if (count > 0)
                {
                    wbPart.Workbook.Save();
                }
            }

            if (count > 0)
            {
                File.Move(tmp, path, overwrite: true);
            }
            else
            {
                try { File.Delete(tmp); } catch { } // 변경이 없으면 원본을 그대로 둔다.
            }
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        return count;
    }

    // 값 자동 타이핑: 수식('=', 인젝션 가드+_xlfn 정규화) / 숫자 / 텍스트. 셀 스타일(서식)은 보존.
    private static void SetValue(Cell cell, string value)
    {
        cell.CellFormula = null;
        cell.CellValue = null;
        cell.InlineString = null;
        cell.DataType = null;

        if (value.Length > 1 && value[0] == '=')
        {
            var expr = value[1..];
            if (!XlsxCreateTool.IsDangerousFormula(expr))
            {
                cell.CellFormula = new CellFormula(XlsxCreateTool.NormalizeFormula(expr));
                return;
            }
            // 위험 수식은 텍스트로.
        }
        else if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var num))
        {
            cell.DataType = CellValues.Number;
            cell.CellValue = new CellValue(num.ToString(CultureInfo.InvariantCulture));
            return;
        }

        cell.DataType = CellValues.InlineString;
        cell.InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
    }

    // 워크시트에서 참조 셀을 찾거나(없으면) 열/행 순서를 유지해 생성한다(유효 xlsx 요구).
    private static Cell GetOrCreateCell(Worksheet ws, string cellRef, uint rowIdx)
    {
        var sheetData = ws.GetFirstChild<SheetData>() ?? ws.AppendChild(new SheetData());

        var row = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex is not null && r.RowIndex == rowIdx);
        if (row is null)
        {
            row = new Row { RowIndex = rowIdx };
            var after = sheetData.Elements<Row>().FirstOrDefault(r => r.RowIndex is not null && r.RowIndex > rowIdx);
            sheetData.InsertBefore(row, after);
        }

        var cell = row.Elements<Cell>().FirstOrDefault(c =>
            string.Equals(c.CellReference?.Value, cellRef, StringComparison.OrdinalIgnoreCase));
        if (cell is null)
        {
            var targetCol = ColIndex(cellRef);
            var after = row.Elements<Cell>().FirstOrDefault(c => ColIndex(c.CellReference?.Value ?? "A1") > targetCol);
            cell = new Cell { CellReference = cellRef };
            row.InsertBefore(cell, after);
        }

        return cell;
    }

    // 셀참조의 열 문자 → 0-based 인덱스("A"→0, "B"→1, "AA"→26).
    private static int ColIndex(string cellRef)
    {
        var idx = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsLetter(ch))
            {
                break;
            }

            idx = idx * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }

        return idx - 1;
    }
}
