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
