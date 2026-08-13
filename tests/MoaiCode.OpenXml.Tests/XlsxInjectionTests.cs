using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

// 수식 인젝션 방어: 정상 수식은 라이브 수식(<f>)으로, DDE/명령 인젝션 페이로드는 텍스트로 기록되어야 한다.
public sealed class XlsxInjectionTests : IDisposable
{
    private readonly string _dir;
    public XlsxInjectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-xlsxinj-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Safe_formulas_stay_formulas_dangerous_become_text()
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var input = new
        {
            path = "s.xlsx",
            sheets = new object[]
            {
                new
                {
                    name = "S",
                    rows = new object[]
                    {
                        new[] { "=SUM(1,2)", "=A1+5" },              // A1, B1: 정상 수식
                        new[] { "=cmd|'/c calc'!A0", "=+1+2" },      // A2, B2: 인젝션
                        new[] { "=@SUM(1)", "=DDE(\"x\",\"y\",\"z\")" }, // A3, B3: 인젝션
                    },
                },
            },
        };
        await foreach (var p in new XlsxCreateTool().ExecuteAsync(JsonSerializer.SerializeToElement(input), ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                Assert.False(o.IsError, o.Text);
            }
        }

        var cells = LoadCells(Path.Combine(_dir, "s.xlsx"));
        Assert.True(HasFormula(cells, "A1"), "=SUM should be a formula");
        Assert.True(HasFormula(cells, "B1"), "=A1+5 should be a formula");
        Assert.False(HasFormula(cells, "A2"), "=cmd| must NOT be a formula");
        Assert.False(HasFormula(cells, "B2"), "=+1+2 must NOT be a formula");
        Assert.False(HasFormula(cells, "A3"), "=@ must NOT be a formula");
        Assert.False(HasFormula(cells, "B3"), "=DDE must NOT be a formula");
    }

    private static Dictionary<string, Cell> LoadCells(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wsPart = doc.WorkbookPart!.WorksheetParts.First();
        var map = new Dictionary<string, Cell>();
        foreach (var c in wsPart.Worksheet.Descendants<Cell>())
        {
            if (c.CellReference?.Value is { } r)
            {
                map[r] = c;
            }
        }

        return map;
    }

    private static bool HasFormula(Dictionary<string, Cell> cells, string reference) =>
        cells.TryGetValue(reference, out var c) && c.CellFormula is not null;
}
