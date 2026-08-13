using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

public sealed class XlsxEditToolTests : IDisposable
{
    private readonly string _dir;
    public XlsxEditToolTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-xlsxedit-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Sets_cells_by_reference_typed_and_stays_valid()
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);

        // 1) 템플릿 통합문서 생성.
        var create = JsonSerializer.SerializeToElement(new
        {
            path = "t.xlsx",
            sheets = new object[]
            {
                new { name = "폼", rows = new object[] { new[] { "이름", "{{name}}" }, new[] { "금액", "0" } } },
            },
        });
        await foreach (var _ in new XlsxCreateTool().ExecuteAsync(create, ctx, CancellationToken.None)) { }

        // 2) 셀 편집: 텍스트/숫자/수식/인젝션.
        var edit = JsonSerializer.SerializeToElement(new
        {
            path = "t.xlsx",
            cells = new object[]
            {
                new { cell = "B1", value = "홍길동" },                 // 텍스트
                new { cell = "B2", value = "1200000" },                // 숫자
                new { cell = "C2", value = "=B2*1.1" },                // 수식
                new { cell = "D2", value = "=cmd|'/c calc'!A0" },      // 인젝션 → 텍스트
                new { cell = "E5", value = "새 셀" },                   // 존재하지 않던 행/셀 생성
            },
        });
        var msg = new System.Text.StringBuilder();
        await foreach (var p in new XlsxEditTool().ExecuteAsync(edit, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                Assert.False(o.IsError, o.Text);
                msg.Append(o.Text);
            }
        }

        var path = Path.Combine(_dir, "t.xlsx");
        using var doc = SpreadsheetDocument.Open(path, false);

        var errs = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errs.Count == 0, "validation errors: " + string.Join(" | ", errs.Select(e => e.Description)));

        var ws = doc.WorkbookPart!.WorksheetParts.First();
        var cells = ws.Worksheet.Descendants<Cell>()
            .Where(c => c.CellReference?.Value is not null)
            .ToDictionary(c => c.CellReference!.Value!, c => c);

        Assert.Equal("홍길동", cells["B1"].InlineString!.Text!.Text);
        Assert.Equal("1200000", cells["B2"].CellValue!.Text);
        Assert.Equal("B2*1.1", cells["C2"].CellFormula!.Text);
        Assert.Null(cells["D2"].CellFormula);                                // 인젝션은 수식 아님
        Assert.Equal(CellValues.InlineString, cells["D2"].DataType!.Value);
        Assert.Equal("새 셀", cells["E5"].InlineString!.Text!.Text);       // 신규 셀 생성됨
    }
}
