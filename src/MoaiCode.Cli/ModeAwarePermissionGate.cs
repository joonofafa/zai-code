using System.Text.Json;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
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
    private readonly IRiskClassifier? _classifier;
    private readonly IPermissionRuleStore? _rules;

    public ModeAwarePermissionGate(
        AgentRuntimeState state,
        IPermissionGate inner,
        string? workspace = null,
        bool confine = false,
        IPermissionGate? confirmer = null,
        IRiskClassifier? classifier = null,
        IPermissionRuleStore? rules = null)
    {
        _state = state;
        _inner = inner;
        _workspace = Path.GetFullPath(workspace ?? Directory.GetCurrentDirectory());
        _confine = confine;
        _confirmer = confirmer;
        _classifier = classifier;
        _rules = rules;
    }

    public async ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        // Plan: 비-읽기전용 차단.
        if (_state.Mode is AgentMode.Plan or AgentMode.Analysis && !tool.IsReadOnly)
        {
            return false;
        }

        // 영속 규칙(Claude Code permissions.allow/deny). deny 는 하드 차단, allow 는 확인/분류기 모두 건너뛴다.
        // allow 가 확인 티어보다 먼저 오는 것이 핵심 — 신뢰한 ssh moai-ec2 를 매번 묻지 않게 한다.
        // (단 rm -rf / 류의 파괴적 하드 차단은 BashTool.BashSecurity.Check 가 실행 시점에 여전히 막는다.)
        switch (_rules?.Evaluate(tool, call))
        {
            case RuleMatch.Deny:
                Console.Error.WriteLine(L10n.Get("permission.denyRule"));
                return false;
            case RuleMatch.Allow:
                return true;
        }

        // 워크스페이스 밖 Write/Edit(대상 경로) 또는 Glob/Grep(검색 루트): 권한 모드와 무관하게 반드시 확인.
        // (빈 작업 디렉토리에서 부모/형제로 헤매는 것을 막는 안전 경계.)
        if (_confine && WritesOutsideWorkspace(tool, call))
        {
            // 비대화형(프롬프트 불가)에서는 안전하게 거부.
            return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
        }

        // 원격 실행/전송(ssh/scp 등) + 파괴적이지만 정당할 수 있는 명령(rm -r, git push --force,
        // terraform destroy…)은 모드와 무관하게 항상 확인. 차단이 아니라 사람이 한 번 보게 하는 것.
        if (TryConfirmReason(tool, call) is { } reason)
        {
            Console.Error.WriteLine(L10n.Get("permission.confirmNeeded", reason));
            return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
        }

        // 여기부터는 규칙 티어를 통과한 호출. 권한 모드가 자동 승인하려 한다면, 규칙이 모르는 위험을
        // 맥락으로 한 번 더 판정한다(LLM 분류기). 사람이 어차피 물어보는 ask 모드에선 건너뛴다.
        var wouldAutoApprove = _state.Mode == AgentMode.AutoAct || _inner is AutoApproveGate;
        if (wouldAutoApprove && !tool.IsReadOnly && _classifier is not null && !IsHarmlessRead(tool, call))
        {
            return await ClassifyAsync(tool, call, ct).ConfigureAwait(false);
        }

        // AutoAct: 권한 자동 승인 (고위험 명령은 BashSecurity, 시스템경로는 PathSafety가 여전히 차단).
        if (_state.Mode == AgentMode.AutoAct)
        {
            return true;
        }

        return await _inner.AllowAsync(tool, call, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 분류기 판정. 실패(null)는 fail-closed — 대화형이면 사람에게 확인, 헤드리스면 거부.
    /// 분류기에 넘기는 것은 명령/인자와 사용자 원문 요청뿐. 툴 출력은 넘기지 않는다(인젝션 차단).
    /// </summary>
    private async ValueTask<bool> ClassifyAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        var verdict = await _classifier!
            .ClassifyAsync(tool.Name, DescribeArgs(call), _state.LastUserRequest, ct)
            .ConfigureAwait(false);

        if (verdict is null)
        {
            WriteReasonLine(L10n.Get("permission.classifierFailed"));
            return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
        }

        switch (verdict.Decision)
        {
            case RiskDecision.Allow:
                return true;

            case RiskDecision.Deny:
                WriteReasonLine(L10n.Get("permission.deniedReason", verdict.Reason));
                return false;

            default:
                WriteReasonLine(L10n.Get("permission.confirmReason", verdict.Reason));
                return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
        }
    }

    // 분류기 사유 메시지 출력. 대화형 터미널에선 진행 스피너(stdout, CR로 라인 유지)가 돌고 있을 수 있어,
    // 먼저 현재 줄을 지워(CR+EL) 스피너 잔상과 한 줄에 붙는 것을 막는다. 리다이렉트(헤드리스)면 이스케이프 생략.
    private static void WriteReasonLine(string msg)
    {
        var prefix = Console.IsOutputRedirected ? string.Empty : "\r\x1b[2K";
        Console.Error.WriteLine(prefix + msg);
    }

    // 대화형이면 사람에게 묻고, 비대화형(confirmer 없음)이면 거부.
    private async ValueTask<bool> ConfirmAsync(ITool tool, ToolUseBlock call, CancellationToken ct) =>
        _confirmer is not null && await _confirmer.AllowAsync(tool, call, ct).ConfigureAwait(false);

    /// <summary>
    /// `ls`/`cat`/`echo` 같은 조회성 Bash 명령은 분류기(=LLM 왕복)를 태우지 않는다.
    /// 단 셸 연산자가 있으면 첫 토큰이 명령을 대표하지 못한다(`echo x > /etc/passwd` 는 쓰기다).
    /// </summary>
    private static bool IsHarmlessRead(ITool tool, ToolUseBlock call)
    {
        if (!string.Equals(tool.Name, "Bash", StringComparison.Ordinal))
        {
            return false;
        }

        var command = ReadString(call, "command");
        return command is not null
            && !PermissionRule.HasShellOperators(command)
            && BashSecurity.IsReadOnlyCommand(command);
    }

    // Bash 명령이 원격 실행/전송·파괴적 명령이면 그 사유, 아니면 null.
    private static string? TryConfirmReason(ITool tool, ToolUseBlock call)
    {
        if (!string.Equals(tool.Name, "Bash", StringComparison.Ordinal))
        {
            return null;
        }

        var command = ReadString(call, "command");
        return command is null ? null : BashSecurity.ConfirmationReason(command);
    }

    private static string DescribeArgs(ToolUseBlock call) =>
        ReadString(call, "command") ?? call.Input.GetRawText();

    // Write/Edit 의 대상 경로, 또는 Glob/Grep 의 검색 루트가 워크스페이스 밖이면 true.
    // (Glob/Grep 포함 이유: 빈 작업 디렉토리에서 부모/형제 디렉토리를 뒤지며 무관한 프로젝트로
    //  헤매는 것을 초기에 멈추고 사용자에게 확인시키기 위함. 읽기(Read)는 메모리/설정 등 정당한
    //  외부 단일파일 접근이 있어 제외.)
    private bool WritesOutsideWorkspace(ITool tool, ToolUseBlock call)
    {
        if (tool.Name is not ("Write" or "Edit" or "Glob" or "Grep"))
        {
            return false;
        }

        var path = ReadString(call, "path");
        return !string.IsNullOrWhiteSpace(path) && PathSafety.IsOutsideWorkspace(_workspace, path);
    }

    private static string? ReadString(ToolUseBlock call, string property)
    {
        if (call.Input.ValueKind != JsonValueKind.Object ||
            !call.Input.TryGetProperty(property, out var e) ||
            e.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return e.GetString();
    }
}
