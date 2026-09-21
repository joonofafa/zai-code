using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
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
    public async Task Docx_paragraph_indent_and_newlines_render()
    {
        await Run(new DocxCreateTool(), new
        {
            path = "indent.docx",
            blocks = new object[]
            {
                new { type = "paragraph", text = "3. 세부 사항은 다음과 같습니다." },
                new { type = "paragraph", text = "가. 대상 서비스: 연구개발망 Gitlab", indent = 1 },
                new { type = "paragraph", text = "첫 줄\n둘째 줄" }, // \n → 줄바꿈(w:br)
            },
        });

        using var doc = WordprocessingDocument.Open(Path.Combine(_dir, "indent.docx"), false);
        Assert.Equal(0, Validate(doc));
        var body = doc.MainDocumentPart!.Document.Body!;

        // indent:1 문단에 좌측 들여쓰기(720twips=0.5")가 적용됨.
        var indented = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .First(p => p.InnerText.StartsWith("가."));
        Assert.Equal("720", indented.ParagraphProperties!
            .GetFirstChild<DocumentFormat.OpenXml.Wordprocessing.Indentation>()!.Left!.Value);

        // 텍스트 내 \n 이 줄바꿈(w:br)으로 렌더됨.
        Assert.NotEmpty(body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Break>());
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
    public async Task Created_docs_embed_readable_moai_docid()
    {
        // 생성 3종 모두 MoaiDocId(GUID)를 심고, 다시 읽어낼 수 있어야 한다(같은 대화 되찾기용).
        await Run(new DocxCreateTool(), new { path = "id.docx", title = "T", paragraphs = new[] { "p" } });
        await Run(new XlsxCreateTool(), new { path = "id.xlsx", sheets = new[] { new { name = "s", rows = new[] { new[] { "a" } } } } });
        await Run(new PptxCreateTool(), new { path = "id.pptx", slides = new[] { new { title = "t", bullets = new[] { "b" } } } });

        foreach (var f in new[] { "id.docx", "id.xlsx", "id.pptx" })
        {
            var id = OfficeDocId.Read(Path.Combine(_dir, f));
            Assert.False(string.IsNullOrWhiteSpace(id), $"{f}: MoaiDocId 없음");
            Assert.True(System.Guid.TryParse(id, out _), $"{f}: GUID 형식 아님 ({id})");
        }

        // 서로 다른 문서는 서로 다른 id.
        Assert.NotEqual(OfficeDocId.Read(Path.Combine(_dir, "id.docx")),
                        OfficeDocId.Read(Path.Combine(_dir, "id.xlsx")));

        // 외부(우리가 안 심은) 파일은 null.
        await File.WriteAllTextAsync(Path.Combine(_dir, "plain.txt"), "hi");
        Assert.Null(OfficeDocId.Read(Path.Combine(_dir, "plain.txt")));
    }

    [Fact]
    public async Task Created_docs_carry_theme_stamp()
    {
        // 생성 3종은 테마(색·폰트)를 커스텀 속성으로 심어, COM 편집 툴이 기본값으로 읽을 수 있어야 한다.
        await Run(new PptxCreateTool(), new { path = "th.pptx", accent = "#E83D45", bg = "#FFFFFF", text_color = "#1F2937",
            slides = new[] { new { title = "t", bullets = new[] { "b" } } } });
        await Run(new DocxCreateTool(), new { path = "th.docx", accent = "E83D45", title = "T", paragraphs = new[] { "p" } });
        await Run(new XlsxCreateTool(), new { path = "th.xlsx", body_font = "Malgun Gothic", sheets = new[] { new { name = "s", rows = new[] { new[] { "a" } } } } });

        var ppt = DocThemeStamp.Read(Path.Combine(_dir, "th.pptx"))!;
        Assert.Equal("E83D45", ppt.Accent);
        Assert.Equal("FFFFFF", ppt.Bg);
        Assert.Equal("1F2937", ppt.Text);
        Assert.False(string.IsNullOrEmpty(ppt.TitleFont));

        var doc = DocThemeStamp.Read(Path.Combine(_dir, "th.docx"))!;
        Assert.Equal("E83D45", doc.Accent);

        var xls = DocThemeStamp.Read(Path.Combine(_dir, "th.xlsx"))!;
        Assert.Equal("Malgun Gothic", xls.BodyFont);

        // 스탬프 후에도 문서는 유효하다.
        Assert.Equal("E83D45", DocThemeStamp.Read(Path.Combine(_dir, "th.pptx"))!.Accent);
        using var p = PresentationDocument.Open(Path.Combine(_dir, "th.pptx"), false);
        Assert.Equal(0, Validate(p));
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
    public async Task Xlsx_auto_types_formula_percent_thousands()
    {
        await Run(new XlsxCreateTool(), new
        {
            path = "typed.xlsx",
            sheets = new[]
            {
                new
                {
                    name = "s",
                    rows = new[]
                    {
                        new[] { "항목", "값" },        // 헤더(텍스트)
                        new[] { "성장률", "12.5%" },    // 퍼센트
                        new[] { "매출", "1,234,567" },  // 천단위
                        new[] { "합계", "=B2+B3" },     // 수식
                    },
                    boldHeader = true,
                },
            },
        });

        using var doc = SpreadsheetDocument.Open(Path.Combine(_dir, "typed.xlsx"), false);
        Assert.Equal(0, Validate(doc));
        var cells = doc.WorkbookPart!.WorksheetParts.First().Worksheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToList();

        DocumentFormat.OpenXml.Spreadsheet.Cell C(string reference) =>
            cells.First(c => c.CellReference == reference);

        // 퍼센트: 값 0.125(문자열 아님) + 서식 스타일 적용.
        Assert.Equal(DocumentFormat.OpenXml.Spreadsheet.CellValues.Number, C("B2").DataType!.Value);
        Assert.Equal("0.125", C("B2").CellValue!.InnerText);
        Assert.NotNull(C("B2").StyleIndex);

        // 천단위: 콤마 제거된 숫자 + 서식.
        Assert.Equal("1234567", C("B3").CellValue!.InnerText);
        Assert.NotNull(C("B3").StyleIndex);

        // 수식: 라이브 CellFormula.
        Assert.NotNull(C("B4").CellFormula);
        Assert.Equal("B2+B3", C("B4").CellFormula!.InnerText);
    }

    [Fact]
    public async Task Xlsx_column_formats_apply_to_formula_results()
    {
        await Run(new XlsxCreateTool(), new
        {
            path = "cols.xlsx",
            sheets = new[]
            {
                new
                {
                    name = "s",
                    boldHeader = true,
                    formats = new[] { "", "won", "percent" }, // A 자동, B 통화, C 퍼센트
                    rows = new[]
                    {
                        new[] { "항목", "금액", "비중" },
                        new[] { "매출", "1200000", "=B2/1000000" }, // 수식 결과가 퍼센트 서식이어야
                    },
                },
            },
        });

        using var doc = SpreadsheetDocument.Open(Path.Combine(_dir, "cols.xlsx"), false);
        Assert.Equal(0, Validate(doc)); // 커스텀 numFmt 포함 유효

        var cells = doc.WorkbookPart!.WorksheetParts.First().Worksheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().ToList();
        DocumentFormat.OpenXml.Spreadsheet.Cell C(string r) => cells.First(c => c.CellReference == r);

        // 통화 열: 숫자 + 커스텀 스타일(>=5).
        Assert.Equal("1200000", C("B2").CellValue!.InnerText);
        Assert.True(C("B2").StyleIndex!.Value >= 5);

        // 퍼센트 열의 수식: CellFormula + 퍼센트 커스텀 스타일이 붙어 결과가 %로 표시됨.
        Assert.NotNull(C("C2").CellFormula);
        Assert.True(C("C2").StyleIndex!.Value >= 5);

        // 커스텀 numFmt 가 스타일시트에 등록됐는지(원화·퍼센트 코드).
        var codes = doc.WorkbookPart.WorkbookStylesPart!.Stylesheet
            .Descendants<DocumentFormat.OpenXml.Spreadsheet.NumberingFormat>()
            .Select(n => n.FormatCode!.Value).ToList();
        Assert.Contains(codes, c => c!.Contains("원"));
        Assert.Contains(codes, c => c == "0.0%");
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
    public async Task Pptx_korean_language_uses_malgun_gothic_default_font()
    {
        // 앱 언어=ko 면 문서 기본 폰트가 Calibri Light 가 아니라 맑은 고딕이어야 한다(빌 리포트).
        var prev = L10n.CurrentLanguage;
        try
        {
            L10n.SetLanguage("ko");
            await Run(new PptxCreateTool(), new
            {
                path = "ko.pptx",
                slides = new[] { new { title = "2026년 진행 보고", bullets = new[] { "요점 1" } } },
            });

            using var doc = PresentationDocument.Open(Path.Combine(_dir, "ko.pptx"), false);
            Assert.Equal(0, Validate(doc));

            // 테마 major/minor: Latin·EA 모두 맑은 고딕(Calibri Light/Calibri 대체).
            var theme = doc.PresentationPart!.SlideMasterParts.First().ThemePart!.Theme;
            var fs = theme.ThemeElements!.FontScheme!;
            Assert.Equal("Malgun Gothic", fs.MajorFont!.LatinFont!.Typeface!.Value);
            Assert.Equal("Malgun Gothic", fs.MinorFont!.LatinFont!.Typeface!.Value);
            Assert.Equal("Malgun Gothic", fs.MajorFont.EastAsianFont!.Typeface!.Value);
            Assert.DoesNotContain("Calibri", theme.OuterXml); // 어디에도 Calibri 잔존 금지

            // 제목 런: Latin·EA 폰트가 맑은 고딕이고 프루핑 언어 태그가 ko-KR.
            var slide = doc.PresentationPart.SlideParts.First().Slide;
            var titleRun = slide.Descendants<DocumentFormat.OpenXml.Drawing.Run>()
                .First(r => r.Text?.Text == "2026년 진행 보고");
            var rp = titleRun.RunProperties!;
            Assert.Equal("Malgun Gothic", rp.GetFirstChild<DocumentFormat.OpenXml.Drawing.LatinFont>()!.Typeface!.Value);
            Assert.Equal("Malgun Gothic", rp.GetFirstChild<DocumentFormat.OpenXml.Drawing.EastAsianFont>()!.Typeface!.Value);
            Assert.Equal("ko-KR", rp.Language!.Value);
        }
        finally
        {
            L10n.SetLanguage(prev);
        }
    }

    [Fact]
    public async Task Docx_korean_language_uses_malgun_gothic_default_font()
    {
        var prev = L10n.CurrentLanguage;
        try
        {
            L10n.SetLanguage("ko");
            await Run(new DocxCreateTool(),
                new { path = "ko.docx", title = "2026년 보고", paragraphs = new[] { "본문 내용" } });

            using var doc = WordprocessingDocument.Open(Path.Combine(_dir, "ko.docx"), false);
            Assert.Equal(0, Validate(doc));

            // DocDefaults 기본 폰트: 라틴·EastAsia 모두 맑은 고딕(Arial 대체).
            var def = doc.MainDocumentPart!.StyleDefinitionsPart!.Styles!
                .Descendants<DocumentFormat.OpenXml.Wordprocessing.RunFonts>().First();
            Assert.Equal("Malgun Gothic", def.Ascii!.Value);
            Assert.Equal("Malgun Gothic", def.EastAsia!.Value);

            // 본문 런도 라틴·EA 모두 맑은 고딕으로 통일.
            var runFonts = doc.MainDocumentPart.Document.Body!
                .Descendants<DocumentFormat.OpenXml.Wordprocessing.Run>()
                .Select(r => r.RunProperties?.RunFonts).First(f => f is not null)!;
            Assert.Equal("Malgun Gothic", runFonts.Ascii!.Value);
            Assert.Equal("Malgun Gothic", runFonts.EastAsia!.Value);
        }
        finally
        {
            L10n.SetLanguage(prev);
        }
    }

    [Fact]
    public async Task Xlsx_korean_language_uses_malgun_gothic_font()
    {
        var prev = L10n.CurrentLanguage;
        try
        {
            L10n.SetLanguage("ko");
            await Run(new XlsxCreateTool(), new
            {
                path = "ko.xlsx",
                sheets = new[] { new { name = "요약", rows = new[] { new[] { "항목", "값" }, new[] { "매출", "120" } } } },
            });

            using var doc = SpreadsheetDocument.Open(Path.Combine(_dir, "ko.xlsx"), false);
            Assert.Equal(0, Validate(doc));
            var names = doc.WorkbookPart!.WorkbookStylesPart!.Stylesheet.Fonts!
                .Descendants<DocumentFormat.OpenXml.Spreadsheet.FontName>()
                .Select(n => n.Val!.Value).ToList();
            Assert.Contains("Malgun Gothic", names); // 스타일시트 폰트가 맑은 고딕(Calibri 대체)
        }
        finally
        {
            L10n.SetLanguage(prev);
        }
    }

    [Fact]
    public async Task Pptx_two_col_heading_with_newlines_does_not_bleed_heading_format()
    {
        // 모델이 heading 에 여러 줄을 \n 으로 뭉쳐 보내는 경우(2026-08-26 "서식 충돌 재발") —
        // 첫 줄만 헤딩(굵게·강조색), 나머지는 본문 불릿 서식이어야 한다.
        await Run(new PptxCreateTool(), new
        {
            path = "twocol.pptx",
            slides = new object[]
            {
                new
                {
                    title = "시장 기회와 차별점",
                    layout = "two_col",
                    columns = new object[]
                    {
                        new { heading = "시장 기회\n생성형 AI 도입이 확대되고 있습니다.\n초기 거버넌스 체계가 유리합니다." },
                        new { heading = "제안 차별점", bullets = new[] { "운영 가능한 산출물로 설계합니다." } },
                    },
                },
            },
        });

        using var doc = PresentationDocument.Open(Path.Combine(_dir, "twocol.pptx"), false);
        Assert.Equal(0, Validate(doc));
        var slide = doc.PresentationPart!.SlideParts.First().Slide;
        var runs = slide.Descendants<DocumentFormat.OpenXml.Drawing.Run>().ToList();

        DocumentFormat.OpenXml.Drawing.Run R(string text) => runs.First(r => r.Text?.Text == text);
        Assert.True(R("시장 기회").RunProperties!.Bold?.Value == true);                       // 첫 줄 = 헤딩
        Assert.NotEqual(true, R("생성형 AI 도입이 확대되고 있습니다.").RunProperties!.Bold?.Value); // 나머지 = 본문
        Assert.NotEqual(true, R("초기 거버넌스 체계가 유리합니다.").RunProperties!.Bold?.Value);
        Assert.DoesNotContain(runs, r => r.Text?.Text?.Contains('\n') == true);               // 개행 뭉침 없음
    }

    [Fact]
    public async Task Pptx_autofit_shrinks_dense_content()
    {
        var manyBullets = Enumerable.Range(1, 16).Select(i => $"요점 {i}").ToArray();
        var manyRows = Enumerable.Range(1, 20) // 20행 → 기본 행높이로는 슬라이드 초과 → 축소돼야
            .Select(i => new[] { $"항목{i}", $"{i * 100}" }).ToArray();

        await Run(new PptxCreateTool(), new
        {
            path = "dense.pptx",
            slides = new object[]
            {
                new { title = "불릿 많은 슬라이드", bullets = manyBullets },
                new { title = "행 많은 표", table = new { headers = new[] { "항목", "값" }, rows = manyRows } },
            },
        });

        using var doc = PresentationDocument.Open(Path.Combine(_dir, "dense.pptx"), false);
        Assert.Equal(0, Validate(doc));
        var slides = doc.PresentationPart!.SlideParts.ToList();

        // 불릿 16개 → 본문 폰트가 기본(1800)보다 작게 축소됐는지.
        var bodyRun = slides[0].Slide.Descendants<DocumentFormat.OpenXml.Drawing.Run>()
            .First(r => r.Text?.Text == "요점 1");
        Assert.True(bodyRun.RunProperties!.FontSize!.Value < 1800);

        // 표 normAutofit: body 텍스트박스에 자동맞춤 속성이 들어갔는지(첫 슬라이드).
        Assert.NotEmpty(slides[0].Slide.Descendants<DocumentFormat.OpenXml.Drawing.NormalAutoFit>());

        // 행 12개 표 → 행 높이가 기본(370840)보다 줄었는지.
        var rowHeights = slides[1].Slide.Descendants<DocumentFormat.OpenXml.Drawing.TableRow>()
            .Select(tr => tr.Height!.Value).ToList();
        Assert.NotEmpty(rowHeights);
        Assert.All(rowHeights, h => Assert.True(h <= 370840));
        Assert.Contains(rowHeights, h => h < 370840); // 실제로 축소됨
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

    // 이미지 헤더 크기 읽기(PNG/JPEG/GIF/BMP) — 디코더 없이 헤더만.
    [Fact]
    public void ImageInfo_ReadsPixelSize_FromHeaders()
    {
        // PNG 640x360
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0x02, 0x80, 0, 0, 0x01, 0x68, 8, 6, 0, 0, 0 };
        Assert.Equal((640, 360), ImageInfo.PixelSize(new MemoryStream(png)));

        // JPEG: SOI, APP0(len 16), SOF0(len 17: precision, H=300, W=500)
        var jpg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        jpg.AddRange(new byte[14]);
        jpg.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 8, 0x01, 0x2C, 0x01, 0xF4, 3 });
        jpg.AddRange(new byte[9]);
        Assert.Equal((500, 300), ImageInfo.PixelSize(new MemoryStream(jpg.ToArray())));

        // GIF 20x10
        var gif = new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 20, 0, 10, 0, 0, 0, 0 };
        Assert.Equal((20, 10), ImageInfo.PixelSize(new MemoryStream(gif)));

        Assert.Null(ImageInfo.PixelSize(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 })));
    }
}
