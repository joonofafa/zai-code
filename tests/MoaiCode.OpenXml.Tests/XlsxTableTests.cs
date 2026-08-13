using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

public sealed class XlsxTableTests : IDisposable
{
    private readonly string _dir;
    public XlsxTableTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-xlsxtbl-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Table_option_creates_valid_listobject_with_autofilter()
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var input = new
        {
            path = "t.xlsx",
            sheets = new object[]
            {
                new
                {
                    name = "요구사항",
                    boldHeader = true,
                    table = true,
                    rows = new object[]
                    {
                        new[] { "ID", "항목", "상태" },
                        new[] { "REQ-1", "로그인", "완료" },
                        new[] { "REQ-2", "검색", "진행" },
                        new[] { "REQ-3", "리포트", "대기" },
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

        var path = Path.Combine(_dir, "t.xlsx");
        using var doc = SpreadsheetDocument.Open(path, false);

        // OpenXML 유효성(테이블/AutoFilter/tableParts 포함해도 유효해야 함).
        var errs = new OpenXmlValidator().Validate(doc).ToList();
        Assert.True(errs.Count == 0, "validation errors: " + string.Join(" | ", errs.Select(e => e.Description)));

        var wsPart = doc.WorkbookPart!.WorksheetParts.First();
        var tParts = wsPart.TableDefinitionParts.ToList();
        Assert.Single(tParts);
        var table = tParts[0].Table;
        Assert.NotNull(table.AutoFilter);
        Assert.Equal("A1:C4", table.Reference!.Value);
        Assert.Equal(3, table.TableColumns!.Count());
        var names = table.TableColumns.Elements<TableColumn>().Select(c => c.Name!.Value!).ToList();
        Assert.Equal(new[] { "ID", "항목", "상태" }, names);
        Assert.Contains(wsPart.Worksheet.Elements<TableParts>(), _ => true);
    }
}
