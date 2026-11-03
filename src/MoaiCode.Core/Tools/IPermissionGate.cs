using MoaiCode.Core.Messages;

namespace MoaiCode.Core.Tools;

/// <summary>
/// 비-읽기전용 툴 실행 전 승인 게이트 (TS의 canUseTool 콜백 대응).
/// 대화형 구현은 사용자에게 다이얼로그를 띄우고, 헤드리스는 자동 승인.
/// </summary>
public interface IPermissionGate
{
    ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct);
}

/// <summary>
/// 읽기 전용 툴 판정 (선택적). 엔진은 읽기 전용 툴에 확인 게이트를 태우지 않지만, 사용자가 명시한 deny 규칙
/// (예: permissions.deny ["WebFetch"])만큼은 지켜야 한다. 구현하지 않은 게이트는 읽기 전용 툴을 모두 허용한다.
/// </summary>
public interface IReadOnlyToolGate
{
    bool DeniesReadOnly(ITool tool, ToolUseBlock call);
}

/// <summary>모든 툴을 자동 승인 (헤드리스/파이프/테스트 기본값).</summary>
public sealed class AutoApproveGate : IPermissionGate
{
    public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
        => ValueTask.FromResult(true);
}

/// <summary>모든 비-읽기전용 툴을 거부 (permission=deny 설정).</summary>
public sealed class DenyAllGate : IPermissionGate
{
    public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
        => ValueTask.FromResult(false);
}

/// <summary>
/// 파괴적 명령 확인(선택적). 일반 확인과 달리 "항상 허용"을 제공하지 않고, 왜 위험한지(reason)를 함께 보여 준다.
/// 권한 모드·allow 규칙과 무관하게 사람이 매번 확인한다(2026-11-03 Bill 결정). 구현하지 않은 확인기는 AllowAsync 로 대신한다.
/// </summary>
public interface ICriticalConfirmGate
{
    ValueTask<bool> ConfirmCriticalAsync(ITool tool, ToolUseBlock call, string reason, CancellationToken ct);
}

/// <summary>
/// 거부 사유를 모델에게 구체적으로 알려 주는 게이트(선택적). 파괴적 명령을 사람이 거절했거나 확인할 사람이 없었을 때
/// 기본 문구("규칙·검사·사용자 중 하나")를 받은 모델이 "허용 규칙을 바꾸면 된다"고 안내하던 것을 막는다.
/// </summary>
public interface IDeniedMessageSource
{
    /// <summary>이 호출의 거부 문구(한 번 꺼내면 지워진다). 없으면 null → 기본 문구.</summary>
    string? TakeDeniedMessage(string toolUseId);
}
