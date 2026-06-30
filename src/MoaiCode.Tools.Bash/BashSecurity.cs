using System.Linq;
using System.Text.RegularExpressions;

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
    private static readonly (Regex Pattern, string Reason)[] Destructive =
    {
        // --- Unix/macOS (bash/zsh) ---
        (Rx(@"\brm\s+(-\w*[rf]\w*\s+)+(/|~|/\*|\$HOME)(\s|$)"), "재귀/강제 삭제가 루트·홈 디렉토리를 대상으로 함"),
        (Rx(@"\brm\s+(-\w*[rf]\w*\s+)+/(boot|bin|sbin|lib|lib64|usr|etc|sys|proc|dev|var|opt|root|run|System|Library)(/|\s|$)"),
            "시스템 디렉토리 재귀/강제 삭제"),
        (Rx(@"\brm\s+(-\w*[rf]\w*\s+)+/\*"), "루트 와일드카드 삭제(/*)"),
        (Rx(@"\brm\s+-[rf]*\s+--no-preserve-root"), "rm --no-preserve-root"),
        (Rx(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:"), "fork bomb"),
        (Rx(@"\bdd\b.*\bof=/dev/(sd|nvme|disk|hd|rdisk)"), "dd가 블록 디바이스에 직접 쓰기"),
        (Rx(@"\bmkfs(\.\w+)?\b"), "파일시스템 포맷(mkfs)"),
        (Rx(@"\b(diskutil)\s+(eraseDisk|eraseVolume|reformat|zeroDisk)\b"), "macOS diskutil 디스크 초기화"),
        (Rx(@">\s*/dev/(sd|nvme|disk|hd|rdisk)"), "블록 디바이스로 리다이렉트"),
        (Rx(@"\bchmod\s+-R\s+0*777\s+/(\s|$)"), "루트에 chmod -R 777"),
        (Rx(@"\b(shutdown|reboot|halt|poweroff)\b"), "시스템 전원/재부팅 명령"),
        (Rx(@"\b(curl|wget)\b[^|]*\|\s*(sudo\s+)?(sh|bash|zsh|pwsh|powershell)\b"), "원격 스크립트 직접 실행(curl|sh)"),
        (Rx(@"\bgit\b.*\bpush\b.*--force\b.*\b(main|master)\b"), "보호 브랜치 강제 푸시"),

        // --- Windows (cmd/powershell) ---
        (Rx(@"\bformat\s+[a-z]:"), "드라이브 포맷(format)"),
        (Rx(@"\bdiskpart\b"), "diskpart(디스크 파티션 조작)"),
        (Rx(@"\bcipher\s+/w"), "cipher /w(디스크 와이프)"),
        (Rx(@"\bbcdedit\b.*\b(delete|deletevalue|/delete)\b"), "부트로더 설정 삭제(bcdedit)"),
        (Rx(@"\b(rd|rmdir)\s+(/[sq]\s+)+([a-z]:\\?(\s|$)|.*\\(Windows|System32|Boot|Program Files))"),
            "Windows 드라이브/시스템 디렉토리 재귀 삭제(rd /s)"),
        (Rx(@"\bdel\s+(/[a-z]\s+)*.*\\(Windows|System32|Boot)\b"), "Windows 시스템 파일 삭제(del)"),
        (Rx(@"Remove-Item\b.*-Recurse\b.*-Force\b.*([a-z]:\\?\s|\\(Windows|System32|Program Files))"),
            "PowerShell 강제 재귀 삭제(드라이브 루트/시스템)"),
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

    /// <summary>원격 실행/전송 등 '항상 확인' 대상이면 true (권한 모드와 무관하게 게이트로 보냄).</summary>
    public static bool NeedsConfirmation(string command)
    {
        var cmd = command?.Trim() ?? string.Empty;
        return cmd.Length > 0 && RemoteExec.Any(p => p.IsMatch(cmd));
    }

    private static Regex Rx(string p) =>
        new(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Verdict Check(string command)
    {
        var cmd = command.Trim();
        if (cmd.Length == 0)
        {
            return new Verdict(false, "빈 명령");
        }

        foreach (var (pattern, reason) in Destructive)
        {
            if (pattern.IsMatch(cmd))
            {
                return new Verdict(false, $"차단된 고위험 명령: {reason}");
            }
        }

        return new Verdict(true, null);
    }

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
