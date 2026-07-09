using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Core.Agent;

/// <summary>모델 스트림 이벤트 (TS의 StreamEvent 대응).</summary>
public abstract record StreamEvent;

public sealed record TextDelta(string Text) : StreamEvent;

public sealed record ToolCallRequested(ToolUseBlock Block) : StreamEvent;

public sealed record TurnCompleted(Usage Usage, string StopReason) : StreamEvent;

/// <summary>툴 실행 완료 (QueryEngine이 디스패치 후 방출, UI 표시용).</summary>
public sealed record ToolExecuted(string ToolName, string ToolUseId, string Output, bool IsError) : StreamEvent;

/// <summary>
/// 프로바이더가 구현하는 채팅 모델 추상화. async generator → IAsyncEnumerable.
/// 의존 방향: Providers → Core (Core는 프로바이더를 모름).
/// </summary>
public interface IChatModel
{
    IAsyncEnumerable<StreamEvent> StreamAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct);
}

/// <summary>
/// 런타임 모델 전환 지원 (선택적). 구현 모델은 사용 중인 모델 id 를 바꾸거나 사용 가능한
/// 모델 목록을 조회할 수 있다. /model 슬래시 명령이 이 인터페이스로 라이브 세션의 모델을 교체한다.
/// 지원하지 않는 모델(예: 오프라인 Echo)은 이 인터페이스를 구현하지 않는다.
/// </summary>
public interface IModelControl
{
    /// <summary>현재 사용 중인 모델 id. set 하면 다음 요청부터 즉시 적용.</summary>
    string CurrentModel { get; set; }

    /// <summary>엔드포인트에서 사용 가능한 모델 id 목록. 실패 시 빈 목록.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);
}
