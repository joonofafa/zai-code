using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

// 생성 → 재오픈 → Open XML 검증. Open XML 은 Office 설치 없이 전 플랫폼에서 완전 검증 가능.
public sealed class CreateRoundTripTests : IDisposable
{
    private readonly string _dir;
    public CreateRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-oxml-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private async Task<string> Run(ITool tool, object input)
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var json = JsonSerializer.SerializeToElement(input);
        var sb = new System.Text.StringBuilder();
        await foreach (var p in tool.ExecuteAsync(json, ctx, CancellationToken.None))
            if (p is ToolOutput o) { Assert.False(o.IsError, o.Text); sb.Append(o.Text); }
        return sb.ToString();
    }

    private static int Validate(OpenXmlPackage doc) => new OpenXmlValidator().Validate(doc).Count();

    [Fact]
    public async Task Docx_creates_valid_document()
    {
        await Run(new DocxCreateTool(),
            new { path = "a.docx", title = "보고서", paragraphs = new[] { "첫 문단", "둘째 문단" } });
        var path = Path.Combine(_dir, "a.docx");
        Assert.True(File.Exists(path));
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal(0, Validate(doc));
        Assert.Contains("첫 문단", doc.MainDocumentPart!.Document.Body!.InnerText);
        Assert.Contains("보고서", doc.MainDocumentPart.Document.Body.InnerText);
    }

    [Fact]
    public async Task Xlsx_creates_valid_workbook_with_numbers_and_text()
    {
        await Run(new XlsxCreateTool(), new
        {
            path = "b.xlsx",
            sheets = new[] { new { name = "매출", rows = new[] {
                new[] { "월", "매출" }, new[] { "1월", "120.5" }, new[] { "2월", "98" } } } }
        });
        var path = Path.Combine(_dir, "b.xlsx");
        using var doc = SpreadsheetDocument.Open(path, false);
        Assert.Equal(0, Validate(doc));
        var sheet = doc.WorkbookPart!.Workbook.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().Single();
        Assert.Equal("매출", sheet.Name!.Value);
    }

    [Fact]
    public async Task Pptx_creates_valid_presentation()
    {
        await Run(new PptxCreateTool(), new
        {
            path = "c.pptx",
            slides = new[] {
                new { title = "제목 슬라이드", bullets = new[] { "요점 1", "요점 2" } },
                new { title = "둘째 슬라이드", bullets = new[] { "내용" } } }
        });
        var path = Path.Combine(_dir, "c.pptx");
        using var doc = PresentationDocument.Open(path, false);
        Assert.Equal(0, Validate(doc)); // ← PPTX 구조가 유효한지 판가름
        Assert.Equal(2, doc.PresentationPart!.SlideParts.Count());
    }

    [Fact]
    public async Task Inspect_reports_structure_and_zero_validation_errors()
    {
        await Run(new DocxCreateTool(), new { path = "d.docx", title = "T", paragraphs = new[] { "p1", "p2", "p3" } });
        var outp = await Run(new OfficeDocInspectTool(), new { path = "d.docx" });
        using var summary = JsonDocument.Parse(outp);
        Assert.Equal("docx", summary.RootElement.GetProperty("Type").GetString());
        Assert.Equal(0, summary.RootElement.GetProperty("ValidationErrors").GetInt32());
        Assert.Equal(4, summary.RootElement.GetProperty("Structure").GetProperty("paragraphCount").GetInt32());
    }

    [Fact]
    public async Task Inspect_rejects_unsupported_extension()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "x.txt"), "hi");
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var json = JsonSerializer.SerializeToElement(new { path = "x.txt" });
        var err = false;
        await foreach (var p in new OfficeDocInspectTool().ExecuteAsync(json, ctx, CancellationToken.None))
            if (p is ToolOutput o) err |= o.IsError;
        Assert.True(err);
    }
}
