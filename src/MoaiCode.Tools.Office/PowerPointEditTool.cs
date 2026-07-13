using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 활성 PowerPoint 의 도형 텍스트를 변경한다(쓰기). 편집 툴의 대표 패턴:
///  - IsReadOnly=false → 쓰기 권한 게이트를 통과한다(ModeAwarePermissionGate).
///  - 쓰기 직전에 대상(프레젠테이션·슬라이드·도형)이 여전히 유효한지 재확인한다.
///    조회 시점과 실행 시점 사이에 사용자가 슬라이드/선택을 바꿀 수 있기 때문(원본 설계 §상태 식별).
/// 현재는 set_text 만 — 위치/서식/추가·삭제는 Phase 2 에서 확장.
/// </summary>
public sealed class PowerPointEditTool : ITool
{
    private readonly StaDispatcher _sta;

    public PowerPointEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "PowerPointEdit";

    public string Description => """
        Edits the running PowerPoint's active presentation (write). Currently: set a shape's text.
        Identify the shape by shape_id (from PowerPointInspect) on a given slide_index; name is a
        fallback. Re-verifies the target exists immediately before writing. Windows only.
        """;

    public bool IsReadOnly => false;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set_text"], "description": "Edit action" },
            "slide_index": { "type": "integer", "description": "1-based slide index" },
            "shape_id": { "type": "integer", "description": "Shape id from PowerPointInspect (preferred)" },
            "shape_name": { "type": "string", "description": "Shape name (fallback if shape_id absent)" },
            "text": { "type": "string", "description": "New text (for set_text)" }
          },
          "required": ["action", "slide_index", "text"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("slide_index")] int? SlideIndex,
        [property: JsonPropertyName("shape_id")] int? ShapeId,
        [property: JsonPropertyName("shape_name")] string? ShapeName,
        [property: JsonPropertyName("text")] string? Text);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("PowerPointEdit: Windows 전용 기능입니다.", IsError: true);
            yield break;
        }

        var inp = input.Deserialize<Input>();
        if (inp is null || inp.SlideIndex is null || inp.Text is null
            || !string.Equals(inp.Action, "set_text", StringComparison.Ordinal))
        {
            yield return new ToolOutput(
                "PowerPointEdit: action=set_text, slide_index, text 가 필요합니다.", IsError: true);
            yield break;
        }

        if (inp.ShapeId is null && string.IsNullOrWhiteSpace(inp.ShapeName))
        {
            yield return new ToolOutput(
                "PowerPointEdit: shape_id 또는 shape_name 중 하나가 필요합니다.", IsError: true);
            yield break;
        }

        string result;
        string? error = null;
        try
        {
            result = await _sta.InvokeAsync(() => SetText(inp)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
        }

        if (error is not null)
        {
            yield return new ToolOutput($"PowerPointEdit: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static string SetText(Input inp)
    {
        dynamic? app = ComInterop.TryGetActiveObject("PowerPoint.Application");
        if (app is null)
        {
            throw new InvalidOperationException("PowerPoint 가 실행 중이 아닙니다.");
        }

        dynamic pres = app.ActivePresentation; // 없으면 COMException

        // 쓰기 직전 재확인 1: 대상 슬라이드가 존재하는가.
        int slideCount = (int)pres.Slides.Count;
        if (inp.SlideIndex!.Value < 1 || inp.SlideIndex.Value > slideCount)
        {
            throw new InvalidOperationException(
                $"슬라이드 {inp.SlideIndex} 없음(현재 {slideCount}개).");
        }

        dynamic slide = pres.Slides[inp.SlideIndex.Value];

        // 쓰기 직전 재확인 2: 대상 도형을 shape_id(우선)/이름으로 다시 찾는다.
        dynamic? shape = FindShape(slide, inp.ShapeId, inp.ShapeName);
        if (shape is null)
        {
            throw new InvalidOperationException("대상 도형을 찾지 못했습니다(슬라이드/선택이 바뀌었을 수 있음).");
        }

        if (!(bool)shape.HasTextFrame)
        {
            throw new InvalidOperationException("텍스트 프레임이 없는 도형입니다.");
        }

        shape.TextFrame.TextRange.Text = inp.Text;
        return $"OK: 슬라이드 {inp.SlideIndex} 도형 '{(string)shape.Name}' 텍스트 변경.";
    }

    private static dynamic? FindShape(dynamic slide, int? shapeId, string? name)
    {
        int count = (int)slide.Shapes.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic s = slide.Shapes[i];
            if (shapeId is not null && (int)s.Id == shapeId.Value)
            {
                return s;
            }

            if (shapeId is null && name is not null
                && string.Equals((string)s.Name, name, StringComparison.Ordinal))
            {
                return s;
            }
        }

        return null;
    }
}
