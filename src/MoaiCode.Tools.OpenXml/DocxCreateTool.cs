using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.OpenXml;

/// <summary>새 Word 문서(.docx)를 만든다. Open XML SDK — Word 설치 불필요, 전 플랫폼.</summary>
public sealed class DocxCreateTool : ITool
{
    public string Name => "DocxCreate";

    public string Description => """
        Creates a new Word document (.docx) with a title and paragraphs. No Word install needed
        (Open XML). Use for generating closed documents; for editing an OPEN document on Windows
        use the PowerPoint/Excel COM tools instead.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Output .docx path (relative to workspace)" },
            "title": { "type": "string", "description": "Document title (bold, first paragraph)" },
            "paragraphs": { "type": "array", "items": { "type": "string" }, "description": "Body paragraphs" }
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("paragraphs")] List<string>? Paragraphs);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("DocxCreate: 'path' is required", IsError: true);
            yield break;
        }

        string full;
        string? error = null;
        try
        {
            full = OpenXmlPaths.ResolveForWrite(context.WorkingDirectory, inp.Path, ".docx");
            Write(full, inp);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            full = string.Empty;
        }

        yield return error is not null
            ? new ToolOutput($"DocxCreate: 실패 — {error}", IsError: true)
            : new ToolOutput($"OK: {full} 생성 ({(inp.Paragraphs?.Count ?? 0)} 문단).");
    }

    private static void Write(string path, Input inp)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document();
        var body = main.Document.AppendChild(new Body());

        if (!string.IsNullOrWhiteSpace(inp.Title))
        {
            body.AppendChild(Paragraph(inp.Title!, bold: true));
        }

        foreach (var p in inp.Paragraphs ?? new List<string>())
        {
            body.AppendChild(Paragraph(p ?? string.Empty, bold: false));
        }
    }

    // 스타일 파트 없이도 valid 하도록 굵기는 RunProperties 로 직접 지정.
    private static Paragraph Paragraph(string text, bool bold)
    {
        var run = new Run();
        if (bold)
        {
            run.AppendChild(new RunProperties(new Bold()));
        }

        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return new Paragraph(run);
    }
}
