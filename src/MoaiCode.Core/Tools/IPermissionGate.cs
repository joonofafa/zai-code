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
