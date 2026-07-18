using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using DA = DocumentFormat.OpenXml.Drawing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 로컬 문서에서 평문 텍스트를 추출한다(청킹 전처리용). 지원: txt/md/csv(네이티브),
/// docx/xlsx/pptx(Open XML), pdf(PdfPig). 스캔 PDF(이미지)는 텍스트가 없어 빈 문자열이 될 수 있다.
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

    private static string ExtractDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var para in body.Descendants<DW.Paragraph>())
        {
            var text = para.InnerText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
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
        foreach (var slidePart in presPart.SlideParts)
        {
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
