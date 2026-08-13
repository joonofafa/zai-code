using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using DW = DocumentFormat.OpenXml.Wordprocessing;
using XL = DocumentFormat.OpenXml.Spreadsheet;
using PP = DocumentFormat.OpenXml.Presentation;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 닫힌 Office 문서(.docx/.xlsx/.pptx)를 열어 구조 요약 + Open XML 유효성 검증 결과를 반환한다(읽기 전용).
/// 생성 문서를 재오픈해 검증하는 용도(원본 설계 §3 문서 생성과 검증).
/// </summary>
public sealed class OfficeDocInspectTool : ITool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "OfficeDocInspect";

    public string Description => """
        Opens a closed Office file (.docx/.xlsx/.pptx) and returns a structure summary plus Open XML
        validation errors (read-only). Use to verify a generated document or inspect an existing one.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": ".docx/.xlsx/.pptx path (relative to workspace)" }
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private sealed record Input([property: JsonPropertyName("path")] string? Path);

    private sealed record Summary(string Type, object Structure, int ValidationErrors, IReadOnlyList<string> FirstErrors);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("OfficeDocInspect: 'path' is required", IsError: true);
            yield break;
        }

        Summary? summary = null;
        string? error = null;
        try
        {
            var full = OpenXmlPaths.ResolveForRead(context.WorkingDirectory, inp.Path);
            summary = Inspect(full);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        yield return error is not null
            ? new ToolOutput(L10n.Get("tools.officeDocInspect.failed", error), IsError: true)
            : new ToolOutput(JsonSerializer.Serialize(summary, JsonOpts));
    }

    private static Summary Inspect(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".docx" => InspectDocx(path),
            ".xlsx" => InspectXlsx(path),
            ".pptx" => InspectPptx(path),
            _ => throw new ArgumentException(L10n.Get("tools.officeDocInspect.unsupportedFormat", ext)),
        };
    }

    private static Summary InspectDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var paras = doc.MainDocumentPart?.Document.Body?.Elements<DW.Paragraph>().ToList()
                    ?? new List<DW.Paragraph>();
        var (errs, first) = Validate(doc);
        return new Summary(
            "docx",
            new { paragraphCount = paras.Count, firstParagraphs = paras.Take(5).Select(p => p.InnerText).ToList() },
            errs, first);
    }

    private static Summary InspectXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var sheets = doc.WorkbookPart?.Workbook.Sheets?.Elements<XL.Sheet>().ToList() ?? new List<XL.Sheet>();
        var sheetInfo = sheets.Select(s =>
        {
            var wsPart = (WorksheetPart)doc.WorkbookPart!.GetPartById(s.Id!);
            var rows = wsPart.Worksheet.Descendants<XL.Row>().Count();
            return new { name = s.Name?.Value, rows };
        }).ToList();
        var (errs, first) = Validate(doc);
        return new Summary("xlsx", new { sheetCount = sheets.Count, sheets = sheetInfo }, errs, first);
    }

    private static Summary InspectPptx(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var slideCount = doc.PresentationPart?.SlideParts.Count() ?? 0;
        var titles = doc.PresentationPart?.SlideParts
            .Select(sp => sp.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().FirstOrDefault()?.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .Take(10).ToList() ?? new List<string?>();
        var (errs, first) = Validate(doc);
        return new Summary("pptx", new { slideCount, firstTexts = titles }, errs, first);
    }

    private static (int Count, IReadOnlyList<string> First) Validate(OpenXmlPackage doc)
    {
        var validator = new OpenXmlValidator();
        var errors = validator.Validate(doc).ToList();
        var first = errors.Take(5).Select(e => $"{e.Id}: {e.Description}").ToList();
        return (errors.Count, first);
    }
}
