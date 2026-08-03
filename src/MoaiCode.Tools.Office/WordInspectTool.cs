using System.Runtime.CompilerServices;
using System.Text.Json;
using MoaiCode.Config;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>실행 중인 Word 의 활성 문서·문단·선택을 조회한다(읽기 전용). Windows 전용.</summary>
public sealed class WordInspectTool : ITool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StaDispatcher _sta;

    public WordInspectTool(StaDispatcher sta) => _sta = sta;

    public string Name => "WordInspect";

    public string Description => """
        Inspects the running Word document: paragraphs (1-based index, text, style, font_size, font_name,
        bold, font_color), floating shapes (1-based index, name, position/size/rotation — for WordEdit
        set_geometry), and current selection. Use BEFORE editing, and MATCH the document's existing
        tone — e.g. give inserted body text the same size/color as surrounding body paragraphs, not a heading.
        Windows only; requires Word running with an open document.
        """;

    public bool IsReadOnly => true;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "max_paragraphs": { "type": "integer", "description": "Max paragraphs to return (default 200)" }
          }
        }
        """).RootElement.Clone();

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("WordInspect: Windows 전용 기능입니다.", IsError: true);
            yield break;
        }

        var maxParas = input.ValueKind == JsonValueKind.Object
                       && input.TryGetProperty("max_paragraphs", out var m)
                       && m.ValueKind == JsonValueKind.Number
            ? System.Math.Clamp(m.GetInt32(), 1, 1000)
            : 200;

        WordDocInfo? info = null;
        string? error = null;
        try
        {
            info = await new WordSession(_sta).GetActiveDocumentAsync(maxParas).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            MoaiLog.Error("WordInspect: inspection threw", ex);
        }

        if (error is not null)
        {
            yield return new ToolOutput($"WordInspect: 조회 실패 — {error}", IsError: true);
            yield break;
        }

        if (info is null)
        {
            yield return new ToolOutput("WordInspect: 실행 중인 Word 에 열린 문서가 없습니다.");
            yield break;
        }

        yield return new ToolOutput(JsonSerializer.Serialize(info, JsonOpts));
    }
}
