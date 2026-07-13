using MoaiCode.Core.Agent;
using MoaiCode.Core.Tools;
using MoaiCode.Persistence;

namespace MoaiCode.Tui.Commands;

// 순환 순서: Act → AutoAct → Plan. AutoAct 는 권한 자동 승인(자율 실행).
public enum AgentMode { Act, AutoAct, Plan }

public sealed class AgentRuntimeState
{
    public AgentMode Mode { get; set; } = AgentMode.Act;

    /// <summary>이번 턴의 사용자 원문 요청. 위험 판정 분류기가 "이 명령이 요청된 일인가"를 볼 때 쓴다.</summary>
    public string? LastUserRequest { get; set; }
}

/// <summary>로그인 계정/호스트/시각/조직명 정보 (/usage 표시용).</summary>
public sealed record AccountInfo(string? Email, string? Host, string? LoginAt, string? OrgName = null);

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
    string ProviderDesc,
    // 런타임 모델 전환(/model). 지원 모델이면 non-null. PersistModel 은 선택한 모델을
    // settings/env 에 저장하는 콜백(Cli 가 주입 — Tui→Config 결합 회피).
    IModelControl? Models = null,
    Action<string>? PersistModel = null,
    // /usage: 모델별 로컬 토큰 사용량 + 로그인 계정/시각.
    UsageStore? Usage = null,
    AccountInfo? Account = null,
    // /permissions: 영속 allow/deny 규칙 조회·편집.
    IPermissionRuleStore? Rules = null);

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
