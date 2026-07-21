using System.Collections.Generic;
using System.Threading;

namespace MoaiCode.Gui.Agent;

/// <summary>
/// GUI 가 대화하는 에이전트 백엔드의 seam. 지금은 StubAgentBackend(UI 개발용 흉내),
/// 다음 단계에서 EngineAgentBackend(MoaiCode.Core.QueryEngine 임베드)로 교체한다.
/// </summary>
public interface IAgentBackend
{
    IAsyncEnumerable<AgentEvent> SendAsync(string prompt, CancellationToken ct);
}

public abstract record AgentEvent;

public sealed record AssistantDelta(string Text) : AgentEvent;

public sealed record ActivityStarted(string Text) : AgentEvent;

public sealed record ActivityDone(string Text) : AgentEvent;

public sealed record DocumentProduced(string Icon, string Kind, string FileName, string Path) : AgentEvent;

public sealed record TurnDone : AgentEvent;
