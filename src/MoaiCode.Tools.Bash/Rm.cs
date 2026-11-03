using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.Bash;

/// <summary>
/// rm 명령의 플래그/대상을 실제로 파싱한다. 정규식으로는 표기 변형을 계속 흘렸다:
/// `rm --recursive --force /`, `rm -fr /`, `rm -rf ~/proj` 가 모두 패턴을 빠져나갔다.
/// 2026-11-03: <see cref="ShellCommand"/> 해석 결과(명령 위치·따옴표·치환 안쪽)를 쓰고, 대상 경로를 정규화한다
/// (`/tmp/../etc`, `//etc`), 같은 줄의 `cd /` 뒤 상대 경로를 풀고(`cd / &amp;&amp; rm -rf etc`), 루트 바로 아래의 글롭
/// (`/e?c`, `/[u]sr`)을 시스템 디렉터리로 본다.
/// </summary>
public static class Rm
{
    /// <summary>되돌릴 수 없는 대상 — 재귀 삭제 시 무조건 차단.</summary>
    private static readonly HashSet<string> CriticalTargets = new(StringComparer.Ordinal)
    {
        "/", "/*", "~", "~/", "~/*", "$HOME", "$HOME/", "$HOME/*",
        "/boot", "/bin", "/sbin", "/lib", "/lib64", "/usr", "/etc", "/sys", "/proc",
        "/dev", "/var", "/opt", "/root", "/run", "/home", "/System", "/Library", "/srv",
    };

    // 이 아래는 재귀 삭제 자체를 막는다(예전 정규식 rmSystemDir 과 같은 범위 — /home 은 사용자 작업이 있어 확인만).
    private static readonly string[] CriticalTrees =
    {
        "/boot/", "/bin/", "/sbin/", "/lib/", "/lib64/", "/usr/", "/etc/", "/sys/", "/proc/", "/dev/", "/var/", "/opt/",
        "/root/", "/run/", "/System/", "/Library/",
    };

    // 하나만 지워도 시스템·감사 기록이 망가지는 위치 — 재귀가 아니어도 확인 대상.
    private static readonly string[] SystemPrefixes =
    {
        "/etc/", "/boot/", "/usr/", "/bin/", "/sbin/", "/lib/", "/lib64/", "/var/", "/opt/", "/root/", "/srv/",
    };

    // 작업 폴더의 빌드 산출물 — 상대 경로로 이것만 지우면 확인하지 않는다(코딩 중 매번 묻지 않게).
    private static readonly HashSet<string> BuildOutputs = new(StringComparer.Ordinal)
    {
        "node_modules", "bin", "obj", "build", "dist", "target", "out", ".next", ".nuxt", ".cache", "__pycache__",
        ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", "coverage", ".gradle", ".turbo", ".parcel-cache",
    };

    /// <summary>재귀 삭제가 루트·홈·시스템 디렉토리를 향하면 true (차단 대상).</summary>
    public static bool IsCriticalDelete(string command)
    {
        foreach (var (recursive, targets) in Parse(command))
        {
            if (!recursive)
            {
                continue;
            }

            foreach (var t in targets)
            {
                if (IsCriticalTarget(t))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCriticalTarget(string t)
    {
        var norm = t.StartsWith('/') ? ShellCommand.NormalizePath(t) : t.TrimEnd('/');
        if (norm.Length == 0)
        {
            norm = "/";
        }

        if (CriticalTargets.Contains(t) || CriticalTargets.Contains(norm)
            || CriticalTrees.Any(d => (norm + "/").StartsWith(d, StringComparison.Ordinal)))
        {
            return true;
        }

        // 루트 바로 아래 이름이 글롭이면(/e?c, /[u]sr, /u*) 시스템 디렉터리를 가리킬 수 있다.
        if (norm.StartsWith('/'))
        {
            var first = norm[1..].Split('/')[0];
            if (first.IndexOfAny(new[] { '*', '?', '[' }) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>재귀 삭제이면 true (차단은 아니고 확인 대상). 대상이 무엇이든.</summary>
    public static bool IsRecursiveDelete(string command) =>
        Parse(command).Any(r => r.Recursive && r.Targets.Count > 0);

    /// <summary>
    /// 확인을 받아야 하는 삭제: 재귀, 와일드카드(rm *.sql), 변수·입력에서 온 대상, 시스템·로그 경로(rm /var/log/secure).
    /// 작업 폴더의 파일 하나(`rm out.txt`)나 빌드 산출물(`rm -rf node_modules bin obj`)은 해당하지 않는다.
    /// </summary>
    public static bool NeedsConfirmation(string command) =>
        Parse(command).Any(r => r.Targets.Count > 0 && !(r.Recursive && r.Targets.All(IsBuildOutput)) && (r.Recursive || r.Targets.Any(IsRisky)));

    private static bool IsBuildOutput(string target)
    {
        if (target.StartsWith('/') || target.StartsWith('~') || target.StartsWith('$') || target.Contains("..", StringComparison.Ordinal)
            || target.IndexOfAny(new[] { '*', '?', '[', '{' }) >= 0)
        {
            return false;
        }

        var last = target.TrimEnd('/');
        var slash = last.LastIndexOf('/');
        return BuildOutputs.Contains(slash < 0 ? last : last[(slash + 1)..]);
    }

    private static bool IsRisky(string target) =>
        target.IndexOfAny(new[] { '*', '?', '[' }) >= 0
        || target.StartsWith('$')
        || target.Contains("{}", StringComparison.Ordinal)
        || target.StartsWith('~')
        || SystemPrefixes.Any(p => (ShellCommand.NormalizePath(target) + "/").StartsWith(p, StringComparison.Ordinal));

    /// <summary>--no-preserve-root 는 명시적 안전장치 해제 → 차단.</summary>
    public static bool DisablesPreserveRoot(string command) =>
        ShellCommand.Commands(command).Any(w => ShellCommand.Program(w[0]) == "rm" && w.Contains("--no-preserve-root"))
        || command.Contains("--no-preserve-root", StringComparison.OrdinalIgnoreCase);

    // 명령 위치에 rm 이 있는 단순 명령마다 (재귀 여부, 대상들). 같은 줄의 cd 로 상대 경로를 푼다.
    private static IEnumerable<(bool Recursive, List<string> Targets)> Parse(string command)
    {
        string? cwd = null;
        foreach (var p in ShellCommand.Parse(command))
        {
            var words = p.Words;
            if (words.Count == 0)
            {
                continue;
            }

            var prog = ShellCommand.Program(words[0]);
            if (prog is "cd" or "pushd" && p.Depth == 0)
            {
                var dir = words.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "~";
                cwd = dir.StartsWith('/') ? ShellCommand.NormalizePath(dir)
                    : dir.StartsWith('~') || dir.StartsWith('$') ? dir
                    : cwd is null ? null
                    : ShellCommand.NormalizePath(cwd.TrimEnd('/') + "/" + dir);
                continue;
            }

            if (prog != "rm")
            {
                continue;
            }

            var recursive = false;
            var endOfOptions = false;
            var targets = new List<string>();
            foreach (var token in words.Skip(1))
            {
                if (!endOfOptions && token == "--")
                {
                    endOfOptions = true;
                    continue;
                }

                if (!endOfOptions && token.StartsWith("--", StringComparison.Ordinal))
                {
                    recursive |= token is "--recursive" or "--dir";
                    continue;
                }

                if (!endOfOptions && token.StartsWith('-') && token.Length > 1)
                {
                    recursive |= token[1..].Any(c => c is 'r' or 'R' or 'd');
                    continue;
                }

                // 같은 줄에서 cd 로 옮긴 곳이 확실하면 상대 경로를 그 아래로 푼다(cd / && rm -rf etc → /etc).
                var t = cwd is not null && cwd.StartsWith('/') && !token.StartsWith('/') && !token.StartsWith('~') && !token.StartsWith('$')
                    ? ShellCommand.NormalizePath(cwd.TrimEnd('/') + "/" + token)
                    : token;
                targets.Add(t);
            }

            yield return (recursive, targets);
        }
    }
}
