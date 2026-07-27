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
    public async Task Docx_blocks_headings_lists_table_and_inline_are_valid()
    {
        await Run(new DocxCreateTool(), new
        {
            path = "rich.docx",
            title = "분기 실적 **보고서**",
            blocks = new object[]
            {
                new { type = "heading", level = 1, text = "요약" },
                new { type = "paragraph", text = "본 보고서는 *3분기* 실적을 **요약**한다." },
                new { type = "bullets", items = new[] { "매출 증가", "비용 절감" } },
                new { type = "numbered", items = new[] { "첫째", "둘째" } },
                new
                {
                    type = "table",
                    header = true,
                    rows = new object[]
                    {
                        new[] { "항목", "값" },
                        new[] { "매출", "120억" },
                        new[] { "영업이익", "18억" },
                    },
                },
            },
        });

        var path = Path.Combine(_dir, "rich.docx");
        using var doc = WordprocessingDocument.Open(path, false);
        Assert.Equal(0, Validate(doc)); // heading+목록+표+인라인 서식 모두 유효
        var body = doc.MainDocumentPart!.Document.Body!;

        Assert.NotEmpty(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>()); // 표 존재
        Assert.Contains("요약", body.InnerText);
        Assert.Contains("•", body.InnerText);   // 불릿 접두어
        Assert.Contains("1.", body.InnerText);  // 번호 접두어
        Assert.Contains("120억", body.InnerText);

        // 인라인 **bold** 가 실제 Bold run 으로 반영됐는지(제목 자체 볼드 외에 본문 강조).
        var bolds = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Bold>().Count();
        Assert.True(bolds > 0);
        // 헤더 셀 음영이 적용됐는지.
        Assert.NotEmpty(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Shading>());
    }

    [Fact]
    public async Task Extractor_preserves_table_structure_as_markdown()
    {
        await Run(new DocxCreateTool(), new
        {
            path = "tbl.docx",
            blocks = new object[]
            {
                new
                {
                    type = "table",
                    header = true,
                    rows = new object[]
                    {
                        new[] { "항목", "값" },
                        new[] { "매출", "120억" },
                    },
                },
            },
        });

        var text = DocumentTextExtractor.Extract(Path.Combine(_dir, "tbl.docx"));
        Assert.Contains("| 항목 | 값 |", text);       // 마크다운 표 헤더
        Assert.Contains("| --- | --- |", text);       // 헤더 구분선
        Assert.Contains("| 매출 | 120억 |", text);    // 데이터 행
    }

    [Fact]
    public async Task Extractor_marks_sheet_boundaries()
    {
        await Run(new XlsxCreateTool(), new
        {
            path = "multi.xlsx",
            sheets = new[]
            {
                new { name = "요약", rows = new[] { new[] { "A", "B" } } },
                new { name = "상세", rows = new[] { new[] { "C", "D" } } },
            },
        });

        var text = DocumentTextExtractor.Extract(Path.Combine(_dir, "multi.xlsx"));
        Assert.Contains("## 요약", text);
        Assert.Contains("## 상세", text);
    }

    [Fact]
    public async Task Docx_legacy_paragraphs_still_work()
    {
        // blocks 없이 기존 title/paragraphs 경로가 그대로 동작(하위호환).
        await Run(new DocxCreateTool(),
            new { path = "legacy.docx", title = "T", paragraphs = new[] { "p1", "p2" } });
        using var doc = WordprocessingDocument.Open(Path.Combine(_dir, "legacy.docx"), false);
        Assert.Equal(0, Validate(doc));
        Assert.Contains("p1", doc.MainDocumentPart!.Document.Body!.InnerText);
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

        // 레이아웃 → 마스터 역관계(ECMA-376 필수). 검증기는 강제 안 하지만 없으면 PowerPoint 가 '복구' 를 띄운다.
        var master = doc.PresentationPart.SlideMasterParts.First();
        var layout = master.SlideLayoutParts.First();
        Assert.NotEmpty(layout.GetPartsOfType<SlideMasterPart>());

        // 도형에 위치·크기(xfrm)가 있어야 실제로 렌더된다(빈 spPr → 공백 슬라이드 회귀 방지).
        var slide = doc.PresentationPart.SlideParts.First().Slide;
        var shapes = slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>().ToList();
        Assert.NotEmpty(shapes);
        Assert.All(shapes, sh => Assert.NotNull(sh.ShapeProperties?.Transform2D));
        Assert.Contains("제목 슬라이드", slide.InnerText); // 텍스트가 실제로 들어있음
    }

    [Fact]
    public async Task Pptx_table_columns_shapes_are_valid()
    {
        await Run(new PptxCreateTool(), new
        {
            path = "rich.pptx",
            slides = new object[]
            {
                new
                {
                    title = "메달리온",
                    accent = "#B45309",
                    columns = new object[]
                    {
                        new { heading = "Bronze", bullets = new[] { "Raw CSV", "무가공" } },
                        new { heading = "Gold", bullets = new[] { "골든셋", "정제완료" } },
                    },
                    table = new
                    {
                        headers = new[] { "단계", "설명" },
                        rows = new object[] { new[] { "Bronze", "원본" }, new[] { "Gold", "정제" } },
                    },
                    shapes = new object[]
                    {
                        new { type = "roundRect", x = 0.5, y = 6.0, w = 2.0, h = 0.8, fill = "#B45309", text = "Bronze", fontColor = "#FFFFFF", bold = true },
                        new { type = "arrow", x = 2.6, y = 6.2, w = 0.6, h = 0.4, fill = "#999999" },
                        new { type = "roundRect", x = 3.3, y = 6.0, w = 2.0, h = 0.8, fill = "#EAB308", text = "Gold", fontColor = "#000000", bold = true },
                    },
                },
            },
        });

        var path = Path.Combine(_dir, "rich.pptx");
        using var doc = PresentationDocument.Open(path, false);
        Assert.Equal(0, Validate(doc)); // 표+2단+색상+도형 모두 포함 유효
        var slide = doc.PresentationPart!.SlideParts.First().Slide;
        Assert.NotEmpty(slide.Descendants<DocumentFormat.OpenXml.Drawing.Table>());              // 표
        Assert.NotEmpty(slide.Descendants<DocumentFormat.OpenXml.Presentation.GraphicFrame>());  // 표 프레임
        Assert.True(slide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>().Count() >= 5); // 제목+2단+도형들
        Assert.Contains("Bronze", slide.InnerText);
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

    [Theory]
    [InlineData("bar")]
    [InlineData("line")]
    [InlineData("pie")]
    public async Task Xlsx_with_chart_is_valid(string chartType)
    {
        await Run(new XlsxCreateTool(), new
        {
            path = "chart.xlsx",
            sheets = new[]
            {
                new
                {
                    name = "매출",
                    rows = new[]
                    {
                        new[] { "브랜드", "매출" },
                        new[] { "스타벅스", "32353624634" },
                        new[] { "컴포즈", "19806016640" },
                        new[] { "투썸", "25541809258" },
                    },
                    boldHeader = true,
                    charts = new[]
                    {
                        new
                        {
                            type = chartType,
                            title = "브랜드별 매출",
                            categories = "A2:A4",
                            series = new[] { new { values = "B2:B4", nameRef = "B1" } },
                            anchor = "D2",
                        },
                    },
                },
            },
        });

        var path = Path.Combine(_dir, "chart.xlsx");
        using var doc = SpreadsheetDocument.Open(path, false);
        Assert.Equal(0, Validate(doc)); // 차트 포함 Open XML 완전 유효
        var ws = doc.WorkbookPart!.WorksheetParts.First();
        Assert.NotNull(ws.DrawingsPart);
        Assert.NotEmpty(ws.DrawingsPart!.ChartParts);
        // 시리즈/조각에 채우기(색)가 있어야 실제로 막대·조각이 보인다(무색이면 LibreOffice 등에서 안 보임).
        Assert.NotEmpty(ws.DrawingsPart.ChartParts.First().ChartSpace
            .Descendants<DocumentFormat.OpenXml.Drawing.SolidFill>());
    }

    [Fact]
    public async Task Xlsx_bold_header_applies_style()
    {
        await Run(new XlsxCreateTool(), new
        {
            path = "styled.xlsx",
            sheets = new[]
            {
                new { name = "s", rows = new[] { new[] { "H1", "H2" }, new[] { "a", "b" } }, boldHeader = true },
            },
        });
        var path = Path.Combine(_dir, "styled.xlsx");
        using var doc = SpreadsheetDocument.Open(path, false);
        Assert.Equal(0, Validate(doc));
        var firstCell = doc.WorkbookPart!.WorksheetParts.First().Worksheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().First();
        Assert.NotNull(firstCell.StyleIndex); // 헤더 셀에 스타일 적용됨
    }
}
