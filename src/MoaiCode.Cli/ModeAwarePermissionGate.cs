using System.Text.Json;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Tools;
using MoaiCode.Tools.Bash;
using MoaiCode.Tui.Commands;

namespace MoaiCode.Cli;

public sealed class ModeAwarePermissionGate : IPermissionGate
{
    private readonly AgentRuntimeState _state;
    private readonly IPermissionGate _inner;
    private readonly string _workspace;
    private readonly bool _confine;
    private readonly IPermissionGate? _confirmer;

    public ModeAwarePermissionGate(
        AgentRuntimeState state,
        IPermissionGate inner,
        string? workspace = null,
        bool confine = false,
        IPermissionGate? confirmer = null)
    {
        _state = state;
        _inner = inner;
        _workspace = Path.GetFullPath(workspace ?? Directory.GetCurrentDirectory());
        _confine = confine;
        _confirmer = confirmer;
    }

    public async ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        // Plan: 비-읽기전용 차단.
        if (_state.Mode == AgentMode.Plan && !tool.IsReadOnly)
        {
            return false;
        }

        // 워크스페이스 밖 절대경로 Write/Edit: 권한 모드(auto/auto-act)·기본 게이트와 무관하게 반드시 확인.
        if (_confine && WritesOutsideWorkspace(tool, call))
        {
            // 비대화형(프롬프트 불가)에서는 안전하게 거부.
            return _confirmer is not null && await _confirmer.AllowAsync(tool, call, ct).ConfigureAwait(false);
        }

        // 원격 실행/전송(ssh/scp/rsync/kubectl 등)은 로컬 perimeter 를 벗어나므로 모드 무관하게 항상 확인.
        // (auto-act 에서 에이전트가 승인 없이 prod 서버에 들어가 도는 사고 방지.)
        if (NeedsRemoteConfirm(tool, call))
        {
            return _confirmer is not null && await _confirmer.AllowAsync(tool, call, ct).ConfigureAwait(false);
        }

        // AutoAct: 권한 자동 승인 (고위험 명령은 BashSecurity, 시스템경로는 PathSafety가 여전히 차단).
        if (_state.Mode == AgentMode.AutoAct)
        {
            return true;
        }

        return await _inner.AllowAsync(tool, call, ct).ConfigureAwait(false);
    }

    // Bash 명령이 원격 실행/전송(ssh/scp/rsync/kubectl/원격 docker 등)이면 true.
    private static bool NeedsRemoteConfirm(ITool tool, ToolUseBlock call)
    {
        if (tool.Name != "Bash")
        {
            return false;
        }

        if (call.Input.ValueKind != JsonValueKind.Object ||
            !call.Input.TryGetProperty("command", out var ce) ||
            ce.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return BashSecurity.NeedsConfirmation(ce.GetString() ?? string.Empty);
    }

    // Write/Edit 의 대상 경로가 워크스페이스 밖이면 true.
    private bool WritesOutsideWorkspace(ITool tool, ToolUseBlock call)
    {
        if (tool.Name is not ("Write" or "Edit"))
        {
            return false;
        }

        if (call.Input.ValueKind != JsonValueKind.Object ||
            !call.Input.TryGetProperty("path", out var pe) ||
            pe.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var path = pe.GetString();
        return !string.IsNullOrWhiteSpace(path) && PathSafety.IsOutsideWorkspace(_workspace, path);
    }
}
