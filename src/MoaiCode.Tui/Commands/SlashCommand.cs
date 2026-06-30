using MoaiCode.Core.Agent;
using MoaiCode.Persistence;

namespace MoaiCode.Tui.Commands;

// 순환 순서: Act → AutoAct → Plan. AutoAct 는 권한 자동 승인(자율 실행).
public enum AgentMode { Act, AutoAct, Plan }

public sealed class AgentRuntimeState
{
    public AgentMode Mode { get; set; } = AgentMode.Act;
}

/// <summary>슬래시 명령 실행에 필요한 컨텍스트.</summary>
public sealed record SlashContext(
    QueryEngine Engine,
    SessionStore Sessions,
    HistoryStore History,
    CheckpointStore Checkpoints,
    AgentRuntimeState State,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> SkillNames,
    IReadOnlyList<string> McpServers,
    string ProviderDesc);

/// <summary>
/// 슬래시 명령 실행 결과. Output은 호출측이 렌더, Quit이면 REPL 종료.
/// SubmitPrompt가 있으면 호출측이 그 프롬프트로 에이전트 턴을 실행 (프롬프트형 커맨드: /init, /review).
/// </summary>
public sealed record SlashResult(string Output, bool Quit = false, string? SubmitPrompt = null);

/// <summary>슬래시 명령 계약 (TS slash command 대응, 확장 가능 레지스트리).</summary>
public interface ISlashCommand
{
    string Name { get; }
    string Description { get; }
    Task<SlashResult> ExecuteAsync(SlashContext ctx, string[] args, CancellationToken ct);
}
