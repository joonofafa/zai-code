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
