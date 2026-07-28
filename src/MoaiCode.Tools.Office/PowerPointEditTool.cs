using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Config;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 활성 PowerPoint 의 도형을 변경한다(쓰기). 편집 툴의 대표 패턴:
///  - IsReadOnly=false → 쓰기 권한 게이트를 통과한다(ModeAwarePermissionGate).
///  - 쓰기 직전에 대상(프레젠테이션·슬라이드·도형)이 여전히 유효한지 재확인한다.
///    조회 시점과 실행 시점 사이에 사용자가 슬라이드/선택을 바꿀 수 있기 때문(원본 설계 §상태 식별).
/// 액션: set_text(텍스트), set_fill(채우기 색), set_font(글자 색·크기·굵기), set_line(테두리 색·두께).
/// 대상: slide_index + shape_id/shape_name 지정, 또는 (미지정 시) 현재 선택한 도형 전체.
/// </summary>
public sealed class PowerPointEditTool : ITool
{
    // MsoTriState.
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;

    // PpSelectionType.
    private const int PpSelectionShapes = 2;
    private const int PpSelectionText = 3;

    private readonly StaDispatcher _sta;

    public PowerPointEditTool(StaDispatcher sta) => _sta = sta;

    public string Name => "PowerPointEdit";

    public string Description => """
        Edits the running PowerPoint's active presentation (write). Actions:
          - set_text: set a shape's text (needs "text")
          - set_fill: set a shape's fill (background) color (needs "color")
          - set_font: set text color/size/bold (any of "color", "font_size", "bold")
          - set_line: set border line color/weight (any of "color", "line_weight")
        Target the shape by shape_id (from PowerPointInspect) on slide_index; shape_name is a fallback.
        If no shape target is given, the action applies to the CURRENTLY SELECTED shape(s).
        "scope" selects where the shape lives: "slide" (default, body shapes), "layout" (the slide's
        layout background shapes), or "master" (the slide master's background shapes). Use layout/master
        to recolor template decorations (banners, sidebars) that stay unchanged when only body shapes are
        edited — e.g. an overall theme color change. NOTE: editing a master/layout shape affects EVERY
        slide sharing it. Colors are "#RRGGBB" hex or a basic name (red, blue, green, yellow, black,
        white, ...). Re-verifies targets immediately before writing. Windows only.
        """;

    public bool IsReadOnly => false;

    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["set_text", "set_fill", "set_font", "set_line"], "description": "Edit action" },
            "slide_index": { "type": "integer", "description": "1-based slide index (omit to target current selection)" },
            "scope": { "type": "string", "enum": ["slide", "layout", "master"], "description": "Where the target shape lives: slide (default), layout, or master. layout/master require slide_index + shape_id/shape_name." },
            "shape_id": { "type": "integer", "description": "Shape id from PowerPointInspect (preferred)" },
            "shape_name": { "type": "string", "description": "Shape name (fallback if shape_id absent)" },
            "text": { "type": "string", "description": "New text (set_text)" },
            "color": { "type": "string", "description": "#RRGGBB hex or basic color name (set_fill/set_font/set_line)" },
            "font_size": { "type": "number", "description": "Font size in points (set_font)" },
            "bold": { "type": "boolean", "description": "Bold on/off (set_font)" },
            "line_weight": { "type": "number", "description": "Border line weight in points (set_line)" }
          },
          "required": ["action"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("action")] string? Action,
        [property: JsonPropertyName("slide_index")] int? SlideIndex,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("shape_id")] int? ShapeId,
        [property: JsonPropertyName("shape_name")] string? ShapeName,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("font_size")] double? FontSize,
        [property: JsonPropertyName("bold")] bool? Bold,
        [property: JsonPropertyName("line_weight")] double? LineWeight);

    private static readonly string[] Actions = { "set_text", "set_fill", "set_font", "set_line" };

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return new ToolOutput("PowerPointEdit: Windows 전용 기능입니다.", IsError: true);
            yield break;
        }

        var inp = input.Deserialize<Input>();
        var validationError = Validate(inp);
        if (validationError is not null)
        {
            yield return new ToolOutput($"PowerPointEdit: {validationError}", IsError: true);
            yield break;
        }

        string result;
        string? error = null;
        try
        {
            result = await _sta.InvokeAsync(() => Apply(inp!)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
            MoaiLog.Error($"PowerPointEdit: action={inp?.Action} threw", ex);
        }

        if (error is not null)
        {
            yield return new ToolOutput($"PowerPointEdit: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    // 입력 검증. 문제 있으면 사용자용 메시지, 없으면 null.
    private static string? Validate(Input? inp)
    {
        if (inp is null || string.IsNullOrWhiteSpace(inp.Action)
            || !Actions.Contains(inp.Action, StringComparer.Ordinal))
        {
            return "action 은 set_text|set_fill|set_font|set_line 중 하나여야 합니다.";
        }

        // 도형 지정(shape_id/shape_name) 시엔 slide_index 도 필요. 둘 다 없으면 현재 선택을 대상.
        var hasShapeRef = inp.ShapeId is not null || !string.IsNullOrWhiteSpace(inp.ShapeName);
        if (hasShapeRef && inp.SlideIndex is null)
        {
            return "shape_id/shape_name 을 쓰려면 slide_index 도 필요합니다.";
        }

        var scope = inp.Scope ?? "slide";
        if (scope is not ("slide" or "layout" or "master"))
        {
            return "scope 는 slide|layout|master 중 하나여야 합니다.";
        }

        // layout/master 도형은 현재 선택으로 못 잡으므로 명시 지정(slide_index+shape)이 필수.
        if (scope is not "slide" && !hasShapeRef)
        {
            return "scope=layout/master 는 slide_index 와 shape_id/shape_name 지정이 필요합니다.";
        }

        return inp.Action switch
        {
            "set_text" when inp.Text is null => "set_text 에는 text 가 필요합니다.",
            "set_fill" when string.IsNullOrWhiteSpace(inp.Color) => "set_fill 에는 color 가 필요합니다.",
            "set_font" when string.IsNullOrWhiteSpace(inp.Color) && inp.FontSize is null && inp.Bold is null
                => "set_font 에는 color, font_size, bold 중 하나가 필요합니다.",
            "set_line" when string.IsNullOrWhiteSpace(inp.Color) && inp.LineWeight is null
                => "set_line 에는 color 또는 line_weight 가 필요합니다.",
            _ => null,
        };
    }

    private static string Apply(Input inp)
    {
        dynamic? app = ComInterop.TryGetActiveObject("PowerPoint.Application");
        if (app is null)
        {
            throw new InvalidOperationException("PowerPoint 가 실행 중이 아닙니다.");
        }

        dynamic pres = app.ActivePresentation; // 없으면 COMException

        var targets = ResolveTargets(app, pres, inp);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(
                "대상 도형을 찾지 못했습니다. slide_index+shape_id 로 지정하거나, PowerPoint 에서 도형을 선택한 뒤 다시 시도하세요.");
        }

        foreach (var shape in targets)
        {
            ApplyToShape(shape, inp);
        }

        var where = inp.SlideIndex is not null
            ? $"슬라이드 {inp.SlideIndex}" + ((inp.Scope ?? "slide") is var sc && sc != "slide" ? $"({sc})" : string.Empty)
            : "현재 선택";
        return $"OK: {where} 도형 {targets.Count}개에 {inp.Action} 적용.";
    }

    // 대상 도형 목록을 만든다: 지정(slide_index+shape) 하나, 또는 현재 선택 전체.
    private static List<dynamic> ResolveTargets(dynamic app, dynamic pres, Input inp)
    {
        var list = new List<dynamic>();

        var hasShapeRef = inp.ShapeId is not null || !string.IsNullOrWhiteSpace(inp.ShapeName);
        if (hasShapeRef)
        {
            int slideCount = (int)pres.Slides.Count;
            if (inp.SlideIndex!.Value < 1 || inp.SlideIndex.Value > slideCount)
            {
                throw new InvalidOperationException($"슬라이드 {inp.SlideIndex} 없음(현재 {slideCount}개).");
            }

            dynamic slide = pres.Slides[inp.SlideIndex.Value];

            // scope 에 따라 검색할 Shapes 컬렉션 선택: 본문 / 레이아웃 배경 / 마스터 배경.
            dynamic shapesCol = (inp.Scope ?? "slide") switch
            {
                "layout" => slide.CustomLayout.Shapes,
                "master" => slide.CustomLayout.SlideMaster.Shapes,
                _ => slide.Shapes,
            };

            dynamic? shape = FindShape(shapesCol, inp.ShapeId, inp.ShapeName);
            if (shape is not null)
            {
                list.Add(shape);
            }

            return list;
        }

        // 대상 미지정 → 현재 선택한 도형들.
        dynamic sel = app.ActiveWindow.Selection;
        int selType = (int)sel.Type;
        if (selType is PpSelectionShapes or PpSelectionText)
        {
            dynamic shapeRange = sel.ShapeRange;
            int n = (int)shapeRange.Count;
            for (var i = 1; i <= n; i++)
            {
                list.Add(shapeRange[i]);
            }
        }

        return list;
    }

    private static void ApplyToShape(dynamic shape, Input inp)
    {
        switch (inp.Action)
        {
            case "set_text":
                // HasTextFrame 은 MsoTriState — (bool) 직접 캐스팅 금지.
                if ((int)shape.HasTextFrame == MsoFalse)
                {
                    throw new InvalidOperationException($"도형 '{(string)shape.Name}' 은 텍스트 프레임이 없습니다.");
                }

                dynamic textRange = shape.TextFrame.TextRange;

                // 텍스트를 교체하면 (1) AutoFit 이 폰트를 극단 축소(예: 3pt)하거나 (2) 서식이 첫 문자
                // 기준으로 통일될 수 있다. 교체 전 대표 서식(크기·굵기·글꼴명)을 기억했다가 복원해 톤을 유지한다.
                float? keepSize = TryFloat(() => (float)textRange.Font.Size);
                int? keepBold = TryInt(() => (int)textRange.Font.Bold);
                string? keepName = TryStr(() => (string)textRange.Font.Name);

                textRange.Text = inp.Text;

                if (keepSize is > 0)
                {
                    TrySet(() => textRange.Font.Size = keepSize.Value);
                }

                if (keepBold is 0 or -1)
                {
                    TrySet(() => textRange.Font.Bold = keepBold.Value);
                }

                if (!string.IsNullOrEmpty(keepName))
                {
                    TrySet(() => textRange.Font.Name = keepName);
                }

                break;

            case "set_fill":
                shape.Fill.Visible = MsoTrue;
                shape.Fill.Solid();
                shape.Fill.ForeColor.RGB = OfficeColor.ToBgr(inp.Color!);
                break;

            case "set_font":
                if ((int)shape.HasTextFrame == MsoFalse)
                {
                    throw new InvalidOperationException($"도형 '{(string)shape.Name}' 은 텍스트 프레임이 없습니다.");
                }

                dynamic font = shape.TextFrame.TextRange.Font;
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    font.Color.RGB = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.FontSize is not null)
                {
                    font.Size = inp.FontSize.Value;
                }

                if (inp.Bold is not null)
                {
                    font.Bold = inp.Bold.Value ? MsoTrue : MsoFalse;
                }

                break;

            case "set_line":
                shape.Line.Visible = MsoTrue;
                if (!string.IsNullOrWhiteSpace(inp.Color))
                {
                    shape.Line.ForeColor.RGB = OfficeColor.ToBgr(inp.Color);
                }

                if (inp.LineWeight is not null)
                {
                    shape.Line.Weight = (float)inp.LineWeight.Value;
                }

                break;
        }
    }

    private static dynamic? FindShape(dynamic shapes, int? shapeId, string? name)
    {
        int count = (int)shapes.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic s = shapes[i];
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

    // COM 서식 값 캡처/설정 헬퍼(mixed·예외는 조용히 무시).
    private static float? TryFloat(Func<float> get)
    {
        try { return get(); } catch { return null; }
    }

    private static int? TryInt(Func<int> get)
    {
        try { return get(); } catch { return null; }
    }

    private static string? TryStr(Func<string> get)
    {
        try { return get(); } catch { return null; }
    }

    private static void TrySet(Action set)
    {
        try { set(); } catch { }
    }
}
