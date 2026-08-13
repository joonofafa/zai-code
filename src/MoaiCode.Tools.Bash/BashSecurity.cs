using System.Linq;
using System.Text.RegularExpressions;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Bash;

/// <summary>
/// 단순화 셸 보안 (CSHARP_PORT_PLAN.md 4.5 결정).
/// 풀 AST 파싱(TS의 ~200K LOC) 대신 고위험 destructive 패턴 deny-list로 차단하고,
/// 나머지는 권한 모드(ask/auto/deny)에 위임. 정책: 불확실하면 보수적으로.
/// </summary>
public static class BashSecurity
{
    public sealed record Verdict(bool Allowed, string? Reason);

    /// <summary>되돌릴 수 없는 고위험 패턴 — 권한 모드와 무관하게 차단.</summary>
    private static readonly (Regex Pattern, string ReasonKey)[] Destructive =
    {
        // --- Unix/macOS (bash/zsh) ---
        (Rx(@"\brm\s+(-\w*[rf]\w*\s+)+(/|~|/\*|\$HOME)(\s|$)"), "tools.bashSecurity.rmRootHome"),
        (Rx(@"\brm\s+(-\w*[rf]\w*\s+)+/(boot|bin|sbin|lib|lib64|usr|etc|sys|proc|dev|var|opt|root|run|System|Library)(/|\s|$)"),
            "tools.bashSecurity.rmSystemDir"),
        (Rx(@"\brm\s+(-\w*[rf]\w*\s+)+/\*"), "tools.bashSecurity.rmRootWildcard"),
        (Rx(@"\brm\s+-[rf]*\s+--no-preserve-root"), "tools.bashSecurity.rmNoPreserveRoot"),
        (Rx(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:"), "tools.bashSecurity.forkBomb"),
        (Rx(@"\bdd\b.*\bof=/dev/(sd|nvme|disk|hd|rdisk)"), "tools.bashSecurity.ddBlockDevice"),
        (Rx(@"\bmkfs(\.\w+)?\b"), "tools.bashSecurity.mkfs"),
        (Rx(@"\b(diskutil)\s+(eraseDisk|eraseVolume|reformat|zeroDisk)\b"), "tools.bashSecurity.diskutilErase"),
        (Rx(@">\s*/dev/(sd|nvme|disk|hd|rdisk)"), "tools.bashSecurity.redirectBlockDevice"),
        (Rx(@"\bchmod\s+-R\s+0*777\s+/(\s|$)"), "tools.bashSecurity.chmodRoot777"),
        (Rx(@"\b(shutdown|reboot|halt|poweroff)\b"), "tools.bashSecurity.powerCommand"),
        (Rx(@"\b(curl|wget)\b[^|]*\|\s*(sudo\s+)?(sh|bash|zsh|pwsh|powershell)\b"), "tools.bashSecurity.remoteScriptExec"),
        (Rx(@"\bgit\b.*\bpush\b.*--force\b.*\b(main|master)\b"), "tools.bashSecurity.forcePushProtected"),

        // --- Windows (cmd/powershell) ---
        (Rx(@"\bformat\s+[a-z]:"), "tools.bashSecurity.formatDrive"),
        (Rx(@"\bdiskpart\b"), "tools.bashSecurity.diskpart"),
        (Rx(@"\bcipher\s+/w"), "tools.bashSecurity.cipherWipe"),
        (Rx(@"\bbcdedit\b.*\b(delete|deletevalue|/delete)\b"), "tools.bashSecurity.bcdeditDelete"),
        (Rx(@"\b(rd|rmdir)\s+(/[sq]\s+)+([a-z]:\\?(\s|$)|.*\\(Windows|System32|Boot|Program Files))"),
            "tools.bashSecurity.winRdSystem"),
        (Rx(@"\bdel\s+(/[a-z]\s+)*.*\\(Windows|System32|Boot)\b"), "tools.bashSecurity.winDelSystem"),
        (Rx(@"Remove-Item\b.*-Recurse\b.*-Force\b.*([a-z]:\\?\s|\\(Windows|System32|Program Files))"),
            "tools.bashSecurity.psForceRecursive"),
    };

    /// <summary>읽기 전용으로 간주되는 안전한 명령 prefix (권한 자동 허용 후보).</summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.Ordinal)
    {
        "ls", "cat", "pwd", "echo", "grep", "rg", "find", "head", "tail",
        "wc", "stat", "file", "which", "whoami", "date", "env", "tree", "du", "df",
    };

    /// <summary>
    /// 로컬 환경을 벗어나는 원격 실행/전송 — 권한 모드(auto/auto-act)와 무관하게 '확인'을 받아야 한다.
    /// (차단은 아님. 예: 에이전트가 prod 서버에 ssh로 들어가 마음대로 도는 것을 사람이 한 번 막게.)
    /// </summary>
    private static readonly Regex[] RemoteExec =
    {
        Rx(@"(^|\s|;|&|\|)ssh\s"),
        Rx(@"(^|\s|;|&|\|)scp\s"),
        Rx(@"(^|\s|;|&|\|)sftp\s"),
        Rx(@"(^|\s|;|&|\|)rsync\b.*\S+:"),     // rsync 의 remote(host:path) 대상
        Rx(@"(^|\s|;|&|\|)telnet\s"),
        Rx(@"(^|\s|;|&|\|)kubectl\s"),
        Rx(@"(^|\s|;|&|\|)docker\s+(-H|--host)\s"),
        Rx(@"\bDOCKER_HOST="),
    };

    /// <summary>
    /// 파괴적이지만 정당할 수 있는 명령 — 차단하지 않고 '확인'을 받는다.
    /// (rm -rf ~/proj, git push --force feature 처럼 하드 차단하면 도구를 못 쓰게 되는 것들.)
    /// </summary>
    private static readonly (Regex Pattern, string ReasonKey)[] DestructiveConfirm =
    {
        (Rx(@"\bfind\b[^|;&]*\s-delete\b"), "tools.bashSecurity.findDelete"),
        (Rx(@"\bfind\b[^|;&]*-exec\s+rm\b"), "tools.bashSecurity.findExecRm"),
        (Rx(@"\bgit\b[^|;&]*\bpush\b[^|;&]*\s(--force|-f)\b"), "tools.bashSecurity.gitForcePush"),
        (Rx(@"\bgit\b[^|;&]*\breset\b[^|;&]*\s--hard\b"), "tools.bashSecurity.gitResetHard"),
        (Rx(@"\bgit\s+clean\b[^|;&]*-\w*[fd]"), "tools.bashSecurity.gitClean"),
        (Rx(@"\baws\s+s3\s+(rb|rm)\b[^|;&]*--(force|recursive)\b"), "tools.bashSecurity.s3Delete"),
        (Rx(@"\b(aws|gcloud|az)\b[^|;&]*\bdelete\b"), "tools.bashSecurity.cloudDelete"),
        (Rx(@"\bterraform\s+destroy\b"), "tools.bashSecurity.terraformDestroy"),
        (Rx(@"\bdocker\s+(system\s+prune|volume\s+rm|rmi)\b"), "tools.bashSecurity.dockerRemove"),
        (Rx(@"\bsystemctl\s+(stop|disable|mask)\b"), "tools.bashSecurity.serviceStop"),
        (Rx(@"\b(dropdb|DROP\s+(DATABASE|TABLE|SCHEMA))\b"), "tools.bashSecurity.dbDrop"),
        (Rx(@"\bchmod\s+-R\b"), "tools.bashSecurity.chmodRecursive"),
        (Rx(@"\bchown\s+-R\b"), "tools.bashSecurity.chownRecursive"),
        (Rx(@"\btruncate\b[^|;&]*-s\s*0\b"), "tools.bashSecurity.truncateEmpty"),
        // `zellij delete-all-sessions`, `docker container prune-all` 등 일괄 파괴 서브커맨드.
        (Rx(@"\b(delete|destroy|purge|prune|wipe|remove)[-_]?all\b"), "tools.bashSecurity.bulkDeleteSubcommand"),

        // Windows/PowerShell 재귀·강제 삭제 (시스템 경로가 아니어도 '파괴적'이면 확인 — rm -r 와 대칭).
        (Rx(@"\b(del|erase)\b[^|;&]*\s/[a-z]*[sq]"), "tools.bashSecurity.winDelErase"),
        (Rx(@"\b(rd|rmdir)\b[^|;&]*\s/s\b"), "tools.bashSecurity.winRdRecursive"),
        (Rx(@"\bRemove-Item\b[^|;&]*\s-(Recurse|Force)\b"), "tools.bashSecurity.psRemoveItem"),
        (Rx(@"\b(rm|ri|del|rmdir)\b[^|;&]*\s-Recurse\b"), "tools.bashSecurity.psRecursiveDelete"),

        // 워크스페이스 밖 상태를 바꾸는 글로벌/시스템 설치 — 로컬 프로젝트 설치(npm install 등)는 제외.
        (Rx(@"\bnpm\s+(install|i|add|update|up)\b[^|;&]*\s(-g|--global)\b"), "tools.bashSecurity.npmGlobal"),
        (Rx(@"\bpnpm\s+(add|install|update|up)\b[^|;&]*\s(-g|--global)\b"), "tools.bashSecurity.pnpmGlobal"),
        (Rx(@"\byarn\s+global\s+(add|upgrade)\b"), "tools.bashSecurity.yarnGlobal"),
        (Rx(@"\bdotnet\s+tool\s+(install|update)\b[^|;&]*\s(-g|--global)\b"), "tools.bashSecurity.dotnetGlobalTool"),
        (Rx(@"\bpipx\s+install\b"), "tools.bashSecurity.pipxGlobal"),
        (Rx(@"\bpip3?\s+install\b[^|;&]*\s--user\b"), "tools.bashSecurity.pipUser"),
        (Rx(@"\b(cargo|gem)\s+install\b"), "tools.bashSecurity.cargoGemGlobal"),
        (Rx(@"\bgo\s+install\b"), "tools.bashSecurity.goInstall"),
        // 시스템 패키지 관리자(대개 sudo 필요, 시스템 전역 변경).
        (Rx(@"\bapt(-get)?\s+(install|remove|purge|upgrade|full-upgrade)\b"), "tools.bashSecurity.aptChange"),
        (Rx(@"\b(dnf|yum|zypper)\s+(install|remove|erase|update|upgrade)\b"), "tools.bashSecurity.sysPkgChange"),
        (Rx(@"\bpacman\s+-S\b"), "tools.bashSecurity.pacmanInstall"),
        (Rx(@"\bapk\s+(add|del)\b"), "tools.bashSecurity.apkChange"),
        (Rx(@"\bbrew\s+(install|uninstall|upgrade)\b"), "tools.bashSecurity.brewChange"),
        (Rx(@"\bsnap\s+(install|remove)\b"), "tools.bashSecurity.snapChange"),
        (Rx(@"\b(choco|winget|scoop)\s+(install|uninstall|upgrade)\b"), "tools.bashSecurity.winPkgChange"),
    };

    /// <summary>
    /// '항상 확인' 대상이면 true (권한 모드와 무관하게 게이트로 보냄).
    /// 원격 실행/전송 + 파괴적이지만 정당할 수 있는 명령 + 재귀 rm.
    /// </summary>
    public static bool NeedsConfirmation(string command)
    {
        var cmd = command?.Trim() ?? string.Empty;
        if (cmd.Length == 0)
        {
            return false;
        }

        return RemoteExec.Any(p => p.IsMatch(cmd))
            || DestructiveConfirm.Any(d => d.Pattern.IsMatch(cmd))
            || Rm.IsRecursiveDelete(cmd);
    }

    /// <summary>'확인' 사유(표시용). 해당 없으면 null.</summary>
    public static string? ConfirmationReason(string command)
    {
        var cmd = command?.Trim() ?? string.Empty;
        if (cmd.Length == 0)
        {
            return null;
        }

        if (RemoteExec.Any(p => p.IsMatch(cmd)))
        {
            return L10n.Get("tools.bashSecurity.remoteExec");
        }

        foreach (var (pattern, reasonKey) in DestructiveConfirm)
        {
            if (pattern.IsMatch(cmd))
            {
                return L10n.Get(reasonKey);
            }
        }

        return Rm.IsRecursiveDelete(cmd) ? L10n.Get("tools.bashSecurity.recursiveDelete") : null;
    }

    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Verdict Check(string command)
    {
        var cmd = command.Trim();
        if (cmd.Length == 0)
        {
            return new Verdict(false, L10n.Get("tools.bashSecurity.emptyCommand"));
        }

        // rm 은 플래그 표기 변형이 너무 많아 정규식으로는 계속 샌다 → 토큰 파싱으로 판정.
        if (Rm.DisablesPreserveRoot(cmd))
        {
            return new Verdict(false, L10n.Get("tools.bashSecurity.blockedFmt", L10n.Get("tools.bashSecurity.rmNoPreserveRoot")));
        }

        if (Rm.IsCriticalDelete(cmd))
        {
            return new Verdict(false, L10n.Get("tools.bashSecurity.blockedFmt", L10n.Get("tools.bashSecurity.criticalDelete")));
        }

        if (FindDeleteFromRoot.IsMatch(cmd))
        {
            return new Verdict(false, L10n.Get("tools.bashSecurity.blockedFmt", L10n.Get("tools.bashSecurity.findDeleteFromRoot")));
        }

        foreach (var (pattern, reasonKey) in Destructive)
        {
            if (pattern.IsMatch(cmd))
            {
                return new Verdict(false, L10n.Get("tools.bashSecurity.blockedFmt", L10n.Get(reasonKey)));
            }
        }

        return new Verdict(true, null);
    }

    private static readonly Regex FindDeleteFromRoot =
        Rx(@"\bfind\s+(/|~|\$HOME)(\s|$)[^|;&]*(-delete\b|-exec\s+rm\b)");

    public static bool IsReadOnlyCommand(string command)
    {
        var first = FirstToken(command);
        return first is not null && ReadOnlyCommands.Contains(first);
    }

    private static string? FirstToken(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var end = trimmed.IndexOfAny(new[] { ' ', '\t' });
        var token = end < 0 ? trimmed : trimmed[..end];
        var slash = token.LastIndexOf('/');
        return slash < 0 ? token : token[(slash + 1)..];
    }
}
