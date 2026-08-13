using MoaiCode.Core.Agent;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Persistence;

namespace MoaiCode.Tui.Commands;

// 순환 순서: Act → AutoAct → Plan. AutoAct 는 권한 자동 승인(자율 실행).
public enum AgentMode { Act, AutoAct, Plan }

public sealed class AgentRuntimeState
{
    public AgentMode Mode { get; set; } = AgentMode.Act;

    /// <summary>이번 턴의 사용자 원문 요청. 위험 판정 분류기가 "이 명령이 요청된 일인가"를 볼 때 쓴다.</summary>
    public string? LastUserRequest { get; set; }

    /// <summary>브레인스토밍 모드: 화두를 Q&A로 구체화 → 에이전트가 플랜을 생성. /brainstorming 로 토글.</summary>
    public bool Brainstorming { get; set; }

    /// <summary>브레인스토밍 남은 Q&A 턴. 0 이하가 되면 다음 턴에 플랜을 강제 마무리한다.</summary>
    public int BrainstormTurnsLeft { get; set; }

    /// <summary>브레인스토밍 진입 전 모델(종료 시 복원). High 티어로 스왑했을 때만 non-null.</summary>
    public string? BrainstormPrevModel { get; set; }

    /// <summary>브레인스토밍 시작 시점의 플랜 트리 스냅샷(새 플랜 생성 감지용).</summary>
    public string? BrainstormBasePlan { get; set; }
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
    IPermissionRuleStore? Rules = null,
    // /effort: 추론 강도(low|medium|high) 조회·변경. PersistEffort는 선택값을 settings/env에 저장.
    string? ReasoningEffort = null,
    Action<string>? PersistEffort = null,
    // /language: UI 언어를 즉시 바꾸고 사용자 설정에 저장.
    string Language = L10n.DefaultLanguage,
    Action<string>? PersistLanguage = null,
    // /skills sync: 팀 공유 스킬을 서버에서 다시 받아 디스크·라이브 스킬 목록을 갱신. 결과 메시지 반환.
    Func<CancellationToken, Task<string>>? SyncTeamSkills = null,
    // /skills: 로컬 활성/비활성 토글. GetSkillChoices=전체 스킬(이름·출처·현재 활성),
    // SetDisabledSkills=비활성 이름 목록을 저장하고 라이브 SkillTool 재적재 후 상태 메시지 반환.
    Func<IReadOnlyList<(string Name, string Source, bool Enabled)>>? GetSkillChoices = null,
    Func<IReadOnlyList<string>, string>? SetDisabledSkills = null,
    // /login·/logout: 세션 도중 재로그인(자격증명 만료·손상 시 REPL 을 나가지 않아도 되게).
    Func<CancellationToken, Task<string>>? Login = null,
    Func<string>? Logout = null,
    // /install·/uninstall: Windows 셸 통합(PATH + 탐색기 우클릭 메뉴) 설치/제거.
    Func<string>? InstallIntegration = null,
    Func<string>? UninstallIntegration = null,
    // /plan: 현재 실행 계획(Phase 트리) 평문 렌더를 반환. Cli 가 taskStore 기반으로 주입(Tui→Tools 결합 회피).
    Func<string>? PlanTree = null,
    // /model low|mid|high: 난이도 티어 모델 설정(런타임 env + settings.json 영속). model=null 이면 해제.
    Action<string, string?>? PersistTierModel = null);

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
