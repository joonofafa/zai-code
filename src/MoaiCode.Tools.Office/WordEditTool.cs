using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 활성 Word 문서를 편집한다(쓰기). 액션:
///  set_text(선택/문단 텍스트), set_font(색·크기·굵기), set_style(제목/본문 스타일),
///  insert_paragraph(문단 추가), delete_paragraph(문단 삭제), insert_table(표 삽입).
/// 대상: para_index 지정, 없으면 현재 선택. Windows 전용.
/// </summary>
public sealed class WordEditTool : ITool
{
    // WdBuiltinStyle.
    private const int WdStyleNormal = -1;
    private const int WdStyleHeading1 = -2;
    private const int WdStyleHeading2 = -3;
    private const int WdStyleHeading3 = -4;
    private const int WdStyleTitle = -63;

    private readonly StaDispatcher _sta;

    public WordEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "WordEdit";

    public string Description => """
        Edits the running Word document (write). Actions:
          - set_text: replace target text (needs "text")
          - set_font: color/size/bold (any of "color","font_size","bold")
          - set_style: paragraph style ("style": heading1|heading2|heading3|title|normal)
          - insert_paragraph: append a new paragraph at end (needs "text"; optional "style")
          - delete_paragraph: delete paragraph at "para_index"
          - insert_table: insert a table (needs "rows","cols")
        Target the paragraph by 1-based "para_index" (from WordInspect); if omitted, the CURRENT SELECTION.
        Colors are "#RRGGBB" hex or a basic name. Windows only.
        """;

    public bool IsReadOnly => false;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set_text","set_font","set_style","insert_paragraph","delete_paragraph","insert_table"] },
            "para_index": { "type": "integer", "description": "1-based paragraph index (omit to target current selection)" },
            "text": { "type": "string" },
            "color": { "type": "string", "description": "#RRGGBB or basic color name" },
            "font_size": { "type": "number" },
            "bold": { "type": "boolean" },
            "style": { "type": "string", "description": "heading1|heading2|heading3|title|normal" },
            "rows": { "type": "integer" },
            "cols": { "type": "integer" }
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("para_index")] int? ParaIndex,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("font_size")] double? FontSize,
        [property: JsonPropertyName("bold")] bool? Bold,
        [property: JsonPropertyName("style")] string? Style,
        [property: JsonPropertyName("rows")] int? Rows,
        [property: JsonPropertyName("cols")] int? Cols);

    private static readonly string[] Actions =
        { "set_text", "set_font", "set_style", "insert_paragraph", "delete_paragraph", "insert_table" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("WordEdit: Windows 전용 기능입니다.", IsError: true);
            yield break;
        }

        var inp = input.Deserialize<Input>();
        var validationError = Validate(inp);
        if (validationError is not null)
        {
            yield return new ToolOutput($"WordEdit: {validationError}", IsError: true);
            yield break;
        }

        string result;
        string? error = null;
        try
        {
            result = await _sta.InvokeAsync(() => Apply(inp!)).ConfigureAwait(false);
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
            MoaiLog.Error($"WordEdit: action={inp?.Action} threw", ex);
        }

        if (error is not null)
        {
            yield return new ToolOutput($"WordEdit: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, System.StringComparer.Ordinal))
        {
            return "action 은 set_text|set_font|set_style|insert_paragraph|delete_paragraph|insert_table 중 하나여야 합니다.";
        }

        return inp.Action switch
        {
            "set_text" when inp.Text is null => "set_text 에는 text 가 필요합니다.",
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => "set_font 에는 color, font_size, bold 중 하나가 필요합니다.",
            "set_style" when string.IsNullOrWhiteSpace(inp.Style) => "set_style 에는 style 이 필요합니다.",
            "insert_paragraph" when inp.Text is null => "insert_paragraph 에는 text 가 필요합니다.",
            "delete_paragraph" when inp.ParaIndex is null => "delete_paragraph 에는 para_index 가 필요합니다.",
            "insert_table" when inp.Rows is null or < 1 || inp.Cols is null or < 1
                => "insert_table 에는 rows, cols(1 이상)가 필요합니다.",
            _ => null,
        };
    }

    private static string Apply(Input inp)
    {
        dynamic? app = ComInterop.TryGetActiveObject("Word.Application");
        if (app is null)
        {
            throw new System.InvalidOperationException("Word 가 실행 중이 아닙니다.");
        }

        dynamic doc = app.ActiveDocument; // 없으면 COMException

        switch (inp.Action)
        {
            case "insert_paragraph":
            {
                dynamic content = doc.Content;
                content.InsertAfter("\r" + inp.Text);
                if (!string.IsNullOrWhiteSpace(inp.Style))
                {
                    dynamic last = doc.Paragraphs[(int)doc.Paragraphs.Count].Range;
                    last.Style = StyleId(inp.Style!);
                }

                return "OK: 문단을 추가했습니다.";
            }

            case "delete_paragraph":
            {
                int count = (int)doc.Paragraphs.Count;
                if (inp.ParaIndex!.Value < 1 || inp.ParaIndex.Value > count)
                {
                    throw new System.InvalidOperationException($"문단 {inp.ParaIndex} 없음(현재 {count}개).");
                }

                doc.Paragraphs[inp.ParaIndex.Value].Range.Delete();
                return $"OK: 문단 {inp.ParaIndex} 를 삭제했습니다.";
            }

            case "insert_table":
            {
                dynamic tRange = Target(app, doc, inp);
                doc.Tables.Add(tRange, inp.Rows!.Value, inp.Cols!.Value);
                return $"OK: {inp.Rows}x{inp.Cols} 표를 삽입했습니다.";
            }
        }

        // set_text / set_font / set_style — 대상 Range 에 적용.
        dynamic range = Target(app, doc, inp);
        switch (inp.Action)
        {
            case "set_text":
                range.Text = inp.Text;
                break;

            case "set_font":
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    range.Font.Color = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.FontSize is not null)
                {
                    range.Font.Size = (float)inp.FontSize.Value;
                }

                if (inp.Bold is not null)
                {
                    range.Font.Bold = inp.Bold.Value ? 1 : 0;
                }

                break;

            case "set_style":
                range.Style = StyleId(inp.Style!);
                break;
        }

        var scope = inp.ParaIndex is not null ? $"문단 {inp.ParaIndex}" : "현재 선택";
        return $"OK: {scope} 에 {inp.Action} 적용.";
    }

    // 대상 Range: para_index 지정 시 해당 문단, 없으면 현재 선택.
    private static dynamic Target(dynamic app, dynamic doc, Input inp)
    {
        if (inp.ParaIndex is not null)
        {
            int count = (int)doc.Paragraphs.Count;
            if (inp.ParaIndex.Value < 1 || inp.ParaIndex.Value > count)
            {
                throw new System.InvalidOperationException($"문단 {inp.ParaIndex} 없음(현재 {count}개).");
            }

            return doc.Paragraphs[inp.ParaIndex.Value].Range;
        }

        return app.Selection.Range;
    }

    private static int StyleId(string style) => style.Trim().ToLowerInvariant() switch
    {
        "heading1" or "h1" or "제목1" or "제목 1" => WdStyleHeading1,
        "heading2" or "h2" or "제목2" or "제목 2" => WdStyleHeading2,
        "heading3" or "h3" or "제목3" or "제목 3" => WdStyleHeading3,
        "title" or "제목" => WdStyleTitle,
        "normal" or "본문" or "표준" => WdStyleNormal,
        _ => WdStyleNormal,
    };
}
