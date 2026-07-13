using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Office;

/// <summary>
/// 실행 중인 PowerPoint 의 활성 프레젠테이션·슬라이드·도형을 조회한다(읽기 전용).
/// LLM 이 편집 전에 현재 상태를 읽도록 조회와 수정을 분리한다(원본 설계 §툴 설계).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerPointInspectTool : ITool
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly StaDispatcher _sta;

    public PowerPointInspectTool(StaDispatcher sta) => _sta = sta;

    public string Name => "PowerPointInspect";

    public string Description => """
        Inspects the running PowerPoint: active presentation, slides, and shapes (read-only).
        Use BEFORE editing to read current state — shape ids, text, positions. Windows only;
        requires PowerPoint to be running with an open presentation.
        """;

    public bool IsReadOnly => true;

    // COM 은 단일 STA 스레드에서 직렬화된다 — 병렬 안전하지 않음.
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "max_slides": { "type": "integer", "description": "Max slides to return (default 100)" }
          }
        }
        """).RootElement.Clone();

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var maxSlides = input.ValueKind == JsonValueKind.Object
                        && input.TryGetProperty("max_slides", out var m)
                        && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, 500)
            : 100;

        PresentationInfo? info = null;
        string? error = null;
        try
        {
            info = await new PowerPointSession(_sta).GetActivePresentationAsync(maxSlides).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error is not null)
        {
            yield return new ToolOutput($"PowerPointInspect: 조회 실패 — {error}", IsError: true);
            yield break;
        }

        if (info is null)
        {
            yield return new ToolOutput(
                "PowerPointInspect: 실행 중인 PowerPoint 에 열린 프레젠테이션이 없습니다.");
            yield break;
        }

        yield return new ToolOutput(JsonSerializer.Serialize(info, JsonOpts));
    }
}
