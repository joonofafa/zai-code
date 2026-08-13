using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

// 모던 함수 _xlfn. 접두사 정규화: 접두사 없이 쓴 함수가 Excel/LibreOffice 인식용으로 저장되어야 한다.
public sealed class XlsxModernFuncTests : IDisposable
{
    private readonly string _dir;
    public XlsxModernFuncTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-xlsxfn-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Modern_functions_get_xlfn_prefix_classic_do_not()
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var input = new
        {
            path = "f.xlsx",
            sheets = new object[]
            {
                new
                {
                    name = "F",
                    rows = new object[]
                    {
                        new[] { "a", "b" },
                        new[]
                        {
                            "=TEXTJOIN(\",\",TRUE,A1:B1)",       // A2 → _xlfn.TEXTJOIN
                            "=FILTER(A1:A2,A1:A2>0)",            // B2 → _xlfn._xlws.FILTER
                        },
                        new[]
                        {
                            "=SUM(A1:B1)",                        // A3 → 그대로(고전 함수)
                            "=_xlfn.XLOOKUP(1,A1:A2,B1:B2)",     // B3 → 이미 접두사, 중복 안 됨
                        },
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

        var f = Formulas(Path.Combine(_dir, "f.xlsx"));
        Assert.Equal("_xlfn.TEXTJOIN(\",\",TRUE,A1:B1)", f["A2"]);
        Assert.Equal("_xlfn._xlws.FILTER(A1:A2,A1:A2>0)", f["B2"]);
        Assert.Equal("SUM(A1:B1)", f["A3"]);
        Assert.Equal("_xlfn.XLOOKUP(1,A1:A2,B1:B2)", f["B3"]); // 중복 접두사 없음
    }

    private static Dictionary<string, string> Formulas(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var ws = doc.WorkbookPart!.WorksheetParts.First();
        var map = new Dictionary<string, string>();
        foreach (var c in ws.Worksheet.Descendants<Cell>())
        {
            if (c.CellReference?.Value is { } r && c.CellFormula?.Text is { } t)
            {
                map[r] = t;
            }
        }

        return map;
    }
}
