using System.Text.Json;
using MoaiCode.Config;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;
using MoaiCode.Tools;
using MoaiCode.Tools.Bash;
using MoaiCode.Tui.Commands;

namespace MoaiCode.Cli;

/// <summary>
/// 권한 판정의 단일 지점. 순서(2026-11-03 재정비):
/// <list type="number">
/// <item>Plan/분석 모드 — 쓰기·실행 툴 차단(조회 명령 하나는 허용).</item>
/// <item>deny 규칙 — 어느 모드든 차단.</item>
/// <item>하드 차단(rm -rf / 등) — BashTool 이 실행 시점에 막는다. 여기선 감사 로그만 남기고 확인을 띄우지 않는다.</item>
/// <item><b>파괴적 명령</b>(<see cref="BashSecurity.DestructiveReason"/>, 실행할 스크립트 내용 포함) — 모드·allow 규칙·자동 승인과
/// 무관하게 사람이 확인한다("항상 허용" 없음). 사람이 없으면 거부. 배치 자동화는 --ignore-confirm 으로만 통과하고, 그때는
/// 감사 로그를 쓸 수 있어야 한다(못 쓰면 거부). 승인·거부·자동 통과 모두 감사 로그에 남는다.</item>
/// <item>allow 규칙 → 허용. 워크스페이스 밖 쓰기·원격 실행 → 확인. 자동 승인 경로는 LLM 위험 분류기.</item>
/// </list>
/// </summary>
public sealed class ModeAwarePermissionGate : IPermissionGate, IReadOnlyToolGate, IDeniedMessageSource
{
    private readonly AgentRuntimeState _state;
    private readonly IPermissionGate _inner;
    private readonly string _workspace;
    private readonly bool _confine;
    private readonly IPermissionGate? _confirmer;
    private readonly IRiskClassifier? _classifier;
    private readonly IPermissionRuleStore? _rules;
    private readonly bool _ignoreConfirm;
    private readonly Func<string?>? _shellCwd;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _deniedMessages = new(StringComparer.Ordinal);

    // 실행할 스크립트를 읽어 볼 크기 상한. 넘으면 내용을 확인할 수 없으니 묻는다.
    private const long MaxScriptBytes = 1024 * 1024;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pl", ".pm", ".rb", ".js", ".mjs", ".cjs", ".ts", ".php", ".lua", ".r", ".tcl", ".awk",
    };

    private static readonly HashSet<string> SqlExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sql", ".psql", ".ddl", ".dml" };

    private static readonly HashSet<string> ShellNames = new(StringComparer.Ordinal) { "sh", "bash", "zsh", "dash", "ksh", "ash", "mksh", "source", "." };

    /// <param name="ignoreConfirm">--ignore-confirm(배치 자동화). 사람의 확인이 필요한 모든 지점을 자동 승인하고 감사 로그에 남긴다.
    /// 하드 차단·deny 규칙·Plan 모드는 그대로다.</param>
    public ModeAwarePermissionGate(
        AgentRuntimeState state,
        IPermissionGate inner,
        string? workspace = null,
        bool confine = false,
        IPermissionGate? confirmer = null,
        IRiskClassifier? classifier = null,
        IPermissionRuleStore? rules = null,
        bool ignoreConfirm = false,
        Func<string?>? shellCwd = null)
    {
        _state = state;
        _inner = inner;
        _workspace = Path.GetFullPath(workspace ?? Directory.GetCurrentDirectory());
        _confine = confine;
        _confirmer = confirmer;
        _classifier = classifier;
        _rules = rules;
        _ignoreConfirm = ignoreConfirm;
        _shellCwd = shellCwd;
    }

    /// <inheritdoc />
    public string? TakeDeniedMessage(string toolUseId) =>
        _deniedMessages.TryRemove(toolUseId, out var m) ? m : null;

    public bool DeniesReadOnly(ITool tool, ToolUseBlock call)
    {
        if (_rules?.Evaluate(tool, call) != RuleMatch.Deny)
        {
            return false;
        }

        Console.Error.WriteLine(L10n.Get("permission.denyRule"));
        return true;
    }

    private readonly record struct Decision(bool Allowed, bool Audited, string Approval);

    public async ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        var d = await DecideAsync(tool, call, ct).ConfigureAwait(false);

        // 격리 실행: 사람이 이 호출을 직접 승인했거나 --ignore-confirm 배치면 호스트에서, 아니면 격리 안에서 돈다(BashTool 이 꺼내 본다).
        if (d.Allowed && IsBash(tool))
        {
            if (d.Approval == AuditLog.Approvals.User || _ignoreConfirm)
            {
                SandboxPolicy.AllowUnsandboxed(call.Id);
            }
            else if (SandboxPolicy.RequiredButUnavailable && d.Approval != AuditLog.Approvals.None)
            {
                // 격리가 필수인데 쓸 수 없다 — 사람이 승인하지 않은 명령은 돌리지 않는다(사람이 없으면 거부).
                Console.Error.WriteLine(L10n.Get("permission.sandboxUnavailable"));
                d = await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
                if (!d.Allowed)
                {
                    _deniedMessages[call.Id] = Reminders.SandboxUnavailable;
                    return false;
                }

                SandboxPolicy.AllowUnsandboxed(call.Id);
            }
        }

        // 배치 자동화(--ignore-confirm)에서는 확인 없이 지나간 쓰기·실행 툴을 모두 감사 로그에 남긴다.
        // 기록할 수 없으면 실행하지 않는다 — 사람이 보지 않는 실행의 유일한 흔적이기 때문이다.
        if (d.Allowed && !d.Audited && _ignoreConfirm && !tool.IsReadOnly)
        {
            var entry = Entry(tool, call, AuditLog.Outcomes.Ok, d.Approval, null);
            if (!AuditLog.Hold(entry))
            {
                Console.Error.WriteLine(L10n.Get("permission.auditUnavailable", AuditLog.FilePath));
                return false;
            }
        }

        return d.Allowed;
    }

    private async ValueTask<Decision> DecideAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        var command = IsBash(tool) ? ReadString(call, "command") : null;

        // Plan/분석: 쓰기·실행 툴 차단. 단 파일을 바꾸지 않는 조회 명령 하나(ls, cat …)는 읽기와 같으니 허용한다.
        if (_state.Mode is AgentMode.Plan or AgentMode.Analysis && !tool.IsReadOnly)
        {
            var harmless = IsHarmlessRead(tool, call) && _rules?.Evaluate(tool, call) != RuleMatch.Deny;
            return new Decision(harmless, true, AuditLog.Approvals.None);
        }

        // deny 규칙은 어느 모드에서든 하드 차단.
        var rule = _rules?.Evaluate(tool, call);
        if (rule == RuleMatch.Deny)
        {
            Console.Error.WriteLine(L10n.Get("permission.denyRule"));
            if (command is not null)
            {
                Audit(tool, call, AuditLog.Outcomes.Denied, AuditLog.Approvals.None, "deny rule");
            }

            return new Decision(false, true, AuditLog.Approvals.None);
        }

        if (command is not null)
        {
            // 하드 차단 대상은 BashTool 이 같은 판정(BashSecurity.Check)으로 실행 직전에 막고 사유를 돌려준다.
            // 막힐 명령을 사람에게 확인시키는 것은 의미가 없으니 확인 없이 넘기고, 감사 로그에만 남긴다.
            var hard = BashSecurity.Check(command);
            if (!hard.Allowed)
            {
                Audit(tool, call, AuditLog.Outcomes.Blocked, AuditLog.Approvals.None, hard.Reason);
                return new Decision(true, true, AuditLog.Approvals.None);
            }

            var (scriptReason, scriptBlocked) = ScriptCheck(command);
            if (scriptBlocked is not null)
            {
                // 실행할 스크립트 안에 하드 차단 명령 — BashTool 은 스크립트 내용을 보지 못하니 여기서 막는다.
                Console.Error.WriteLine(L10n.Get("tools.bashSecurity.blockedFmt", scriptBlocked));
                Audit(tool, call, AuditLog.Outcomes.Blocked, AuditLog.Approvals.None, scriptBlocked);
                _deniedMessages[call.Id] = Reminders.DestructiveScriptBlocked;
                return new Decision(false, true, AuditLog.Approvals.None);
            }

            // 격리 밖 실행 요청(dangerously_disable_sandbox)은 그 명령을 사람이 직접 승인해야 한다 — 파괴적 명령과 같은 확인.
            var unsandbox = SandboxPolicy.Active && ReadBool(call, "dangerously_disable_sandbox")
                ? L10n.Get("permission.unsandboxedRequest")
                : null;
            var critical = BashSecurity.DestructiveReason(command) ?? scriptReason ?? unsandbox;
            if (critical is not null)
            {
                return await CriticalAsync(tool, call, critical, ct).ConfigureAwait(false);
            }
        }
        else if (tool.Name.StartsWith("mcp__", StringComparison.Ordinal) && McpContentReason(call) is { } mcpReason)
        {
            // MCP 툴(사내 DB·서버 관리 도구 등)의 입력에 파괴적 SQL·명령이 있으면 셸과 같은 기준으로 사람이 확인한다.
            return await CriticalAsync(tool, call, mcpReason, ct).ConfigureAwait(false);
        }

        if (rule == RuleMatch.Allow)
        {
            return new Decision(true, false, AuditLog.Approvals.Rule);
        }

        // 워크스페이스 밖 Write/Edit(대상 경로) 또는 Glob/Grep(검색 루트): 권한 모드와 무관하게 반드시 확인.
        if (_confine && WritesOutsideWorkspace(tool, call))
        {
            return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
        }

        // 원격 실행·전송·클러스터 접속: 모드와 무관하게 확인(신뢰한 대상은 위 allow 규칙이 이미 통과시켰다).
        if (command is not null && BashSecurity.RemoteReason(command) is { } remote)
        {
            Console.Error.WriteLine(L10n.Get("permission.confirmNeeded", remote));
            return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);
        }

        // 자동 승인 경로에서는 규칙이 모르는 위험을 맥락으로 한 번 더 판정한다(LLM 분류기). --ignore-confirm 은 판정을 건너뛴다.
        var wouldAutoApprove = _state.Mode == AgentMode.AutoAct || _inner is AutoApproveGate;
        if (!_ignoreConfirm && wouldAutoApprove && !tool.IsReadOnly && _classifier is not null && !IsHarmlessRead(tool, call))
        {
            return await ClassifyAsync(tool, call, ct).ConfigureAwait(false);
        }

        if (_state.Mode == AgentMode.AutoAct)
        {
            return new Decision(true, false, AuditLog.Approvals.Override);
        }

        var allowed = await _inner.AllowAsync(tool, call, ct).ConfigureAwait(false);
        // 대화형 확인창(터미널·stream-json)은 사람이 이 호출을 본 것이다 — 파괴적 확인을 구현한 게이트가 곧 사람이 답하는 게이트다.
        return new Decision(allowed, false, _inner is ICriticalConfirmGate ? AuditLog.Approvals.User : AuditLog.Approvals.Override);
    }

    // 파괴적 명령: 사람의 확인(항상 허용 없음) → 감사 로그. 사람이 없으면 거부. --ignore-confirm 은 감사 로그를 조건으로 통과.
    private async ValueTask<Decision> CriticalAsync(ITool tool, ToolUseBlock call, string reason, CancellationToken ct)
    {
        Console.Error.WriteLine(L10n.Get("permission.criticalNeeded", reason));

        if (_ignoreConfirm)
        {
            if (AuditLog.Hold(Entry(tool, call, AuditLog.Outcomes.Ok, AuditLog.Approvals.Override, reason)))
            {
                return new Decision(true, true, AuditLog.Approvals.Override);
            }

            Console.Error.WriteLine(L10n.Get("permission.auditUnavailable", AuditLog.FilePath));
            return new Decision(false, true, AuditLog.Approvals.None);
        }

        if (_confirmer is null)
        {
            Console.Error.WriteLine(L10n.Get("permission.criticalNoHuman"));
            Audit(tool, call, AuditLog.Outcomes.Denied, AuditLog.Approvals.None, reason);
            _deniedMessages[call.Id] = Reminders.DestructiveNoHuman;
            return new Decision(false, true, AuditLog.Approvals.None);
        }

        var ok = _confirmer is ICriticalConfirmGate critical
            ? await critical.ConfirmCriticalAsync(tool, call, reason, ct).ConfigureAwait(false)
            : await _confirmer.AllowAsync(tool, call, ct).ConfigureAwait(false);

        if (ok)
        {
            AuditLog.Hold(Entry(tool, call, AuditLog.Outcomes.Ok, AuditLog.Approvals.User, reason));   // 사람이 승인한 것은 기록 실패와 무관하게 진행
        }
        else
        {
            Audit(tool, call, AuditLog.Outcomes.Declined, AuditLog.Approvals.None, reason);
            _deniedMessages[call.Id] = Reminders.DestructiveDeclined;
        }

        return new Decision(ok, true, ok ? AuditLog.Approvals.User : AuditLog.Approvals.None);
    }

    /// <summary>
    /// 분류기 판정. 실패(null)는 fail-closed — 대화형이면 사람에게 확인, 헤드리스면 거부.
    /// 분류기에 넘기는 것은 명령/인자와 사용자 원문 요청뿐. 툴 출력은 넘기지 않는다(인젝션 차단).
    /// </summary>
    private async ValueTask<Decision> ClassifyAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
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
                return new Decision(true, false, AuditLog.Approvals.Override);

            case RiskDecision.Deny:
                WriteReasonLine(L10n.Get("permission.deniedReason", verdict.Reason));
                // 대화형이면 휴리스틱 거부를 사람 확인으로 강등 — 분류기는 사람 대신 결정하지 않는다.
                // 헤드리스(확인 불가)는 ConfirmAsync 가 false → 여전히 하드 거부(fail-closed).
                return await ConfirmAsync(tool, call, ct).ConfigureAwait(false);

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

    // 대화형이면 사람에게 묻고, 비대화형(confirmer 없음)이면 거부. --ignore-confirm 이면 묻지 않고 승인(호출측이 감사 기록).
    private async ValueTask<Decision> ConfirmAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        if (_ignoreConfirm)
        {
            return new Decision(true, false, AuditLog.Approvals.Override);
        }

        var ok = _confirmer is not null && await _confirmer.AllowAsync(tool, call, ct).ConfigureAwait(false);
        return new Decision(ok, false, AuditLog.Approvals.User);
    }

    /// <summary>
    /// `ls`/`cat`/`echo` 같은 조회성 Bash 명령은 분류기(=LLM 왕복)를 태우지 않는다.
    /// 단 셸 연산자가 있으면 첫 토큰이 명령을 대표하지 못한다(`echo x > /etc/passwd` 는 쓰기다).
    /// 인자로 쓰기·실행을 하는 경우(tree -o, rg --pre, date -s)도 조회가 아니다.
    /// </summary>
    private static bool IsHarmlessRead(ITool tool, ToolUseBlock call)
    {
        if (!IsBash(tool))
        {
            return false;
        }

        var command = ReadString(call, "command");
        return command is not null
            && !PermissionRule.HasShellOperators(command)
            && BashSecurity.IsReadOnlyCommand(command)
            && !PermissionRules.WritesOrExecutes(command.Trim());
    }

    /// <summary>
    /// 실행하려는 스크립트·SQL 파일의 내용을 본다 — 모델이 Write 로 스크립트를 쓰고 이름만으로 실행해 확인을 피하는 길을 막는다.
    /// 대상: 경로로 부른 프로그램(./deploy.sh), 셸·인터프리터의 스크립트 인자(bash deploy, python3 x.py, source x),
    /// DB 클라이언트의 파일(psql -f x.sql, -fx.sql, --file=x.sql, sqlplus @x.sql, psql &lt; x.sql).
    /// 상대 경로는 셸이 이어 쓰는 작업 폴더와 같은 줄의 cd 기준으로 푼다.
    /// 반환: (확인 사유, 하드 차단 사유). 셸·인터프리터·DB 가 읽을 파일을 찾지 못하거나 너무 크면 확인(모르면 묻는다).
    /// </summary>
    private (string? Reason, string? Blocked) ScriptCheck(string command)
    {
        var cwd = _shellCwd?.Invoke() ?? _workspace;
        foreach (var p in ShellCommand.Parse(command))
        {
            var w = p.Words;
            if (w.Count == 0 || w[0] == "(code)")
            {
                continue;
            }

            var prog = ShellCommand.Program(w[0]);
            if (prog is "cd" or "pushd" && p.Depth == 0)
            {
                cwd = ResolvePath(cwd, w.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "~");
                continue;
            }

            foreach (var (path, kind, mustRead) in ScriptOperands(p, prog))
            {
                var text = ReadScript(ResolvePath(cwd, path));
                if (text is null)
                {
                    if (mustRead)
                    {
                        return (L10n.Get("permission.scriptUnreadable", path), null);
                    }

                    continue;
                }

                if (kind != BashSecurity.Content.Sql && BashSecurity.Check(text) is { Allowed: false } hard)
                {
                    return (null, L10n.Get("permission.scriptContains", path, hard.Reason ?? string.Empty));
                }

                if (BashSecurity.DestructiveReason(text, kind) is { } reason)
                {
                    return (L10n.Get("permission.scriptContains", path, reason), null);
                }
            }
        }

        return (null, null);
    }

    // (경로, 내용 종류, 꼭 읽어야 하는가). 경로로 부른 프로그램은 바이너리일 수 있어 못 읽어도 묻지 않는다(실행 파일은 분석 대상이 아니다).
    private static IEnumerable<(string Path, BashSecurity.Content Kind, bool MustRead)> ScriptOperands(ShellCommand.Parsed p, string prog)
    {
        var w = p.Words;
        if (w[0].Contains('/'))
        {
            yield return (w[0], KindOf(w[0]), false);
        }

        var isShell = ShellNames.Contains(prog);
        var isCode = prog.StartsWith("python", StringComparison.Ordinal) || prog is "perl" or "ruby" or "node" or "nodejs" or "php" or "lua"
            or "deno" or "bun" or "Rscript" or "tclsh";
        if (isShell || isCode)
        {
            // -c/-e(코드 문자열)·-m(모듈)이 있으면 파일을 읽지 않는다. 첫 일반 인자가 스크립트다.
            if (!w.Skip(1).TakeWhile(a => a.StartsWith('-')).Any(a => a is "-m" or "-e" or "-E" or "--eval" or "-r"
                    || (isShell && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('c'))
                    || (!isShell && a is "-c")))
            {
                var script = w.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
                if (script is not null && script is not ("/dev/stdin" or "-"))
                {
                    yield return (script, isShell ? BashSecurity.Content.ShellScript : BashSecurity.Content.Code, true);
                }
            }

            foreach (var r in p.Redirects.Where(r => r.Op == "<"))
            {
                yield return (r.Target, isShell ? BashSecurity.Content.ShellScript : BashSecurity.Content.Code, true);
            }
        }

        if (prog is "awk" or "gawk")
        {
            for (var i = 1; i + 1 < w.Count; i++)
            {
                if (w[i] == "-f")
                {
                    yield return (w[i + 1], BashSecurity.Content.Code, true);
                }
            }
        }

        if (BashSecurity.DbClients.Contains(prog) || prog == "pg_restore")
        {
            for (var i = 1; i < w.Count; i++)
            {
                var a = w[i];
                if (a is "-f" or "--file" or "-i" or "--input" && i + 1 < w.Count)
                {
                    yield return (w[i + 1], BashSecurity.Content.Sql, true);
                }
                else if (a.StartsWith("--file=", StringComparison.Ordinal))
                {
                    yield return (a[7..], BashSecurity.Content.Sql, true);
                }
                else if (a.StartsWith("-f", StringComparison.Ordinal) && a.Length > 2 && !a.StartsWith("--", StringComparison.Ordinal))
                {
                    yield return (a[2..], BashSecurity.Content.Sql, true);
                }
                else if (a.StartsWith('@') && a.Length > 1)
                {
                    yield return (a[1..], BashSecurity.Content.Sql, true);   // sqlplus @script.sql
                }
            }

            foreach (var r in p.Redirects.Where(r => r.Op == "<"))
            {
                yield return (r.Target, BashSecurity.Content.Sql, true);
            }
        }
    }

    private static BashSecurity.Content KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        return SqlExtensions.Contains(ext) ? BashSecurity.Content.Sql
            : CodeExtensions.Contains(ext) ? BashSecurity.Content.Code
            : BashSecurity.Content.ShellScript;
    }

    private static string ResolvePath(string cwd, string path)
    {
        try
        {
            if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("$HOME", StringComparison.Ordinal))
            {
                var rest = path.StartsWith('~') ? path[1..] : path[5..];
                return Path.GetFullPath(MoaiCode.Core.AppPaths.UserHome + rest);
            }

            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(cwd, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    // 텍스트 스크립트만 읽는다. 없음·너무 큼·바이너리(NUL 포함)·장치 파일이면 null.
    // /dev·/proc·/sys 아래(/dev/tty, /dev/stdin, /dev/zero …)는 열기만 해도 멈추거나 끝없이 읽힌다 — 읽지 않는다(필요하면 묻는다).
    // 이름 붙은 파이프(FIFO)는 여는 순간 멈출 수 있어 2초 안에 못 읽으면 포기한다.
    private static string? ReadScript(string full)
    {
        try
        {
            var target = File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
            if (target.StartsWith("/dev/", StringComparison.Ordinal) || target.StartsWith("/proc/", StringComparison.Ordinal)
                || target.StartsWith("/sys/", StringComparison.Ordinal))
            {
                return null;
            }

            var info = new FileInfo(target);
            if (!info.Exists || info.Length > MaxScriptBytes)
            {
                return null;
            }

            var read = Task.Run(() =>
            {
                using var fs = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buf = new byte[MaxScriptBytes + 1];
                var n = 0;
                int got;
                while (n < buf.Length && (got = fs.Read(buf, n, buf.Length - n)) > 0)
                {
                    n += got;
                }

                return n > MaxScriptBytes ? null : buf[..n];
            });
            if (!read.Wait(TimeSpan.FromSeconds(2)) || read.Result is not { } bytes)
            {
                return null;
            }

            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8192)) >= 0)
            {
                return null;
            }

            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or AggregateException)
        {
            return null;
        }
    }

    // MCP 툴 입력의 문자열 값들에 파괴적 SQL·셸 명령이 있으면 그 사유.
    private static string? McpContentReason(ToolUseBlock call)
    {
        foreach (var value in Strings(call.Input))
        {
            var reason = BashSecurity.DestructiveReason(value, BashSecurity.Content.Sql)
                         ?? BashSecurity.DestructiveReason(value, BashSecurity.Content.ShellScript);
            if (reason is not null)
            {
                return L10n.Get("permission.toolInputContains", reason);
            }
        }

        return null;
    }

    private static IEnumerable<string> Strings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    foreach (var s in Strings(p.Value))
                    {
                        yield return s;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    foreach (var s in Strings(item))
                    {
                        yield return s;
                    }
                }

                break;
        }
    }

    private AuditLog.Entry Entry(ITool tool, ToolUseBlock call, string outcome, string approval, string? reason) =>
        AuditLog.Create(tool.Name, AuditTarget(tool, call), outcome, approval, reason, _state.Mode.ToString(), call.Id,
            ReadString(call, "action"));

    private void Audit(ITool tool, ToolUseBlock call, string outcome, string approval, string? reason) =>
        AuditLog.TryWrite(Entry(tool, call, outcome, approval, reason));

    // 감사 대상: Bash 는 명령, 그 외는 오피스챗과 같은 키 순서로 처음 나오는 문자열.
    private static string AuditTarget(ITool tool, ToolUseBlock call)
    {
        if (IsBash(tool))
        {
            return ReadString(call, "command") ?? "(none)";
        }

        foreach (var key in new[] { "name", "path", "file_path", "to", "url", "query", "subject", "folder", "id" })
        {
            if (ReadString(call, key) is { Length: > 0 } v)
            {
                return v;
            }
        }

        return "(none)";
    }

    private static string DescribeArgs(ToolUseBlock call) =>
        ReadString(call, "command") ?? call.Input.GetRawText();

    private static bool IsBash(ITool tool) => string.Equals(tool.Name, "Bash", StringComparison.Ordinal);

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

    private static bool ReadBool(ToolUseBlock call, string property) =>
        call.Input.ValueKind == JsonValueKind.Object
        && call.Input.TryGetProperty(property, out var e)
        && e.ValueKind == JsonValueKind.True;

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
