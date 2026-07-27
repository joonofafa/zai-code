using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using DA = DocumentFormat.OpenXml.Drawing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 로컬 문서에서 텍스트를 추출한다(청킹·RAG·참조 입력용). 지원: txt/md/csv(네이티브),
/// docx/xlsx/pptx(Open XML), pdf(PdfPig). 스캔 PDF(이미지)는 텍스트가 없어 빈 문자열이 될 수 있다.
/// 구조 보존: docx는 제목/목록/표를 마크다운 마커(#, -, | |)로, xlsx는 시트명, pptx는
/// 슬라이드 경계를 제목으로 표시해 문서 구조가 소실되지 않게 한다.
/// </summary>
public static class DocumentTextExtractor
{
    public static readonly IReadOnlyCollection<string> SupportedExtensions = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".docx", ".xlsx", ".pptx", ".pdf",
    };

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    /// <summary>파일에서 텍스트를 추출한다. 포맷 미지원/파싱 실패는 예외로 던진다.</summary>
    public static string Extract(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" or ".md" or ".csv" => File.ReadAllText(path),
            ".docx" => ExtractDocx(path),
            ".xlsx" => ExtractXlsx(path),
            ".pptx" => ExtractPptx(path),
            ".pdf" => ExtractPdf(path),
            var ext => throw new NotSupportedException($"지원하지 않는 형식: {ext}"),
        };
    }

    // 구조 보존 추출: 최상위 요소를 순서대로 순회한다(표 안 문단이 섞이지 않게).
    // 제목/목록은 마크다운 마커(#, -)로, 표는 마크다운 표(| |)로 살려 청킹·RAG·참조
    // 입력이 문서 구조를 잃지 않게 한다.
    private static string ExtractDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            switch (el)
            {
                case DW.Paragraph para:
                    var text = para.InnerText;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(ParagraphPrefix(para)).AppendLine(text);
                    }

                    break;
                case DW.Table table:
                    AppendMarkdownTable(sb, table);
                    break;
            }
        }

        return sb.ToString();
    }

    // 문단 스타일 → 마크다운 접두어. 진짜 Heading/Title 스타일과 목록(numPr)만 인식한다.
    private static string ParagraphPrefix(DW.Paragraph p)
    {
        var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrEmpty(style))
        {
            var s = style.ToLowerInvariant();
            if (s == "title")
            {
                return "# ";
            }

            if (s.StartsWith("heading"))
            {
                var digits = new string(s.Where(char.IsDigit).ToArray());
                return digits switch { "1" => "# ", "2" => "## ", _ => "### " };
            }
        }

        return p.ParagraphProperties?.NumberingProperties is not null ? "- " : string.Empty;
    }

    private static void AppendMarkdownTable(StringBuilder sb, DW.Table table)
    {
        var rows = table.Elements<DW.TableRow>().ToList();
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Elements<DW.TableCell>()
                .Select(c => c.InnerText.ReplaceLineEndings(" ").Trim())
                .ToList();
            sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
            if (r == 0)
            {
                sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", cells.Count))).AppendLine(" |");
            }
        }

        sb.AppendLine();
    }

    private static string ExtractXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wbPart = doc.WorkbookPart;
        if (wbPart?.Workbook?.Sheets is null)
        {
            return string.Empty;
        }

        var shared = wbPart.SharedStringTablePart?.SharedStringTable;
        var sb = new StringBuilder();
        foreach (var sheet in wbPart.Workbook.Sheets.Elements<Sheet>())
        {
            if (sheet.Id?.Value is not { } relId || wbPart.GetPartById(relId) is not WorksheetPart wsPart)
            {
                continue;
            }

            // 시트 구분(구조 보존): 여러 시트가 하나로 뭉치지 않게 시트명을 제목으로 단다.
            if (!string.IsNullOrEmpty(sheet.Name?.Value))
            {
                sb.Append("## ").AppendLine(sheet.Name!.Value);
            }

            foreach (var row in wsPart.Worksheet.Descendants<Row>())
            {
                var cells = row.Elements<Cell>()
                    .Select(c => CellText(c, shared))
                    .Where(t => t.Length > 0);
                var line = string.Join('\t', cells);
                if (line.Length > 0)
                {
                    sb.AppendLine(line);
                }
            }
        }

        return sb.ToString();
    }

    private static string CellText(Cell cell, SharedStringTable? shared)
    {
        var raw = cell.CellValue?.InnerText ?? cell.InnerText;
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        if (cell.DataType?.Value == CellValues.SharedString
            && shared is not null
            && int.TryParse(raw, out var idx)
            && idx >= 0 && idx < shared.ChildElements.Count)
        {
            return shared.ChildElements[idx].InnerText;
        }

        return raw;
    }

    private static string ExtractPptx(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var presPart = doc.PresentationPart;
        if (presPart is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var slideNo = 1;
        foreach (var slidePart in presPart.SlideParts)
        {
            // 슬라이드 구분(구조 보존): 슬라이드 경계를 제목으로 표시.
            sb.Append("## ").Append("Slide ").AppendLine(slideNo++.ToString());
            foreach (var t in slidePart.Slide.Descendants<DA.Text>())
            {
                if (!string.IsNullOrWhiteSpace(t.Text))
                {
                    sb.AppendLine(t.Text);
                }
            }
        }

        return sb.ToString();
    }

    private static string ExtractPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            // ContentOrderTextExtractor: 읽기 순서에 가깝게 텍스트를 재구성.
            var text = ContentOrderTextExtractor.GetText(page);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
    }
}
