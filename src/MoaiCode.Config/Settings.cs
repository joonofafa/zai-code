using MoaiCode.Core.Tools;

namespace MoaiCode.Config;

/// <summary>
/// 병합된 설정 (3-tier: user → project → env, 뒤로 갈수록 우선).
/// 로딩/머지는 SettingsLoader. 설계 근거: ../../CSHARP_PORT_PLAN.md 4.6.
/// </summary>
public sealed record Settings
{
    public PermissionMode Permission { get; init; } = PermissionMode.Ask;
    public string? Provider { get; init; }
    public string? Model { get; init; }
    public string? BaseUrl { get; init; }

    /// <summary>로그인 호스트 (예: https://vip.bccard.ai). /usage 표시용.</summary>
    public string? Host { get; init; }

    /// <summary>로그인 계정(이메일). 로그인 시 저장. /usage 표시용.</summary>
    public string? Account { get; init; }

    /// <summary>마지막 로그인 시각(ISO 8601). /usage 표시용.</summary>
    public string? LoginAt { get; init; }

    /// <summary>로그인 사용자의 소속 조직명. 로그인 시 서버에서 수신. /usage 표시용.</summary>
    public string? OrgName { get; init; }

    /// <summary>HTTP(S) 프록시 서버 (예: http://proxy.corp:8080). 사내망용. 비번은 credentials(PROXY_PASSWORD).</summary>
    public string? ProxyUrl { get; init; }

    /// <summary>프록시 인증 사용자 (선택). 비번은 credentials 저장소의 PROXY_PASSWORD.</summary>
    public string? ProxyUser { get; init; }

    public int MaxTurns { get; init; } = 12;
    public string? OutputStyle { get; init; }
    public string? LintCommand { get; init; }
    public string? TestCommand { get; init; }
    public bool AutoLint { get; init; }
    public bool AutoTest { get; init; }
    public int RepoMapTokens { get; init; } = 1200;

    /// <summary>비-읽기전용 툴 실행 전 workspace 체크포인트 자동 생성 (끄면 git 오버헤드 제거).</summary>
    public bool Checkpoints { get; init; } = true;

    /// <summary>
    /// 워크스페이스(작업 디렉토리) 밖의 절대경로 Write/Edit 는 권한 모드(auto/auto-act)와 무관하게
    /// 항상 확인받는다. 비대화형이면 거부. 실수로 시스템/홈 파일을 건드리는 사고 방지.
    /// </summary>
    public bool ConfineToWorkspace { get; init; } = true;

    /// <summary>모델 컨텍스트 창(토큰). 컴팩션/복구 임계선의 기준 — 약 70%에서 선제 컴팩션.</summary>
    public int ContextWindowTokens { get; init; } = 200_000;

    public static Settings Default { get; } = new();
}
