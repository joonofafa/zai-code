namespace MoaiCode.Tools.Bash;

/// <summary>
/// rm 명령의 플래그/대상을 실제로 파싱한다. 정규식으로는 표기 변형을 계속 흘렸다:
/// `rm --recursive --force /`, `rm -fr /`, `rm -rf ~/proj` 가 모두 패턴을 빠져나갔다.
/// </summary>
public static class Rm
{
    /// <summary>되돌릴 수 없는 대상 — 재귀 삭제 시 무조건 차단.</summary>
    private static readonly HashSet<string> CriticalTargets = new(StringComparer.Ordinal)
    {
        "/", "/*", "~", "~/", "~/*", "$HOME", "$HOME/", "$HOME/*", "/.",
        "/boot", "/bin", "/sbin", "/lib", "/lib64", "/usr", "/etc", "/sys", "/proc",
        "/dev", "/var", "/opt", "/root", "/run", "/home", "/System", "/Library",
    };

    /// <summary>재귀 삭제가 루트·홈·시스템 디렉토리를 향하면 true (차단 대상).</summary>
    public static bool IsCriticalDelete(string command)
    {
        var (found, recursive, _, targets) = Parse(command);
        if (!found || !recursive)
        {
            return false;
        }

        foreach (var t in targets)
        {
            var norm = t.TrimEnd('/');
            if (norm.Length == 0)
            {
                norm = "/";
            }

            if (CriticalTargets.Contains(t) || CriticalTargets.Contains(norm))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>재귀 삭제이면 true (차단은 아니고 확인 대상). 대상이 무엇이든.</summary>
    public static bool IsRecursiveDelete(string command)
    {
        var (found, recursive, _, targets) = Parse(command);
        return found && recursive && targets.Count > 0;
    }

    /// <summary>--no-preserve-root 는 명시적 안전장치 해제 → 차단.</summary>
    public static bool DisablesPreserveRoot(string command) =>
        command.Contains("--no-preserve-root", StringComparison.OrdinalIgnoreCase);

    private static (bool Found, bool Recursive, bool Force, List<string> Targets) Parse(string command)
    {
        var targets = new List<string>();
        var recursive = false;
        var force = false;
        var found = false;
        var afterRm = false;

        foreach (var raw in Tokenize(command))
        {
            var token = raw;

            if (!afterRm)
            {
                // `sudo rm`, `/bin/rm`, `xargs rm` 등 앞에 붙는 토큰을 건너뛴다.
                if (string.Equals(Basename(token), "rm", StringComparison.Ordinal))
                {
                    afterRm = true;
                    found = true;
                }

                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (token.Equals("--recursive", StringComparison.Ordinal) ||
                    token.Equals("--dir", StringComparison.Ordinal))
                {
                    recursive = true;
                }
                else if (token.Equals("--force", StringComparison.Ordinal))
                {
                    force = true;
                }

                continue;
            }

            if (token.StartsWith('-') && token.Length > 1)
            {
                foreach (var c in token[1..])
                {
                    if (c is 'r' or 'R' or 'd')
                    {
                        recursive = true;
                    }
                    else if (c == 'f')
                    {
                        force = true;
                    }
                }

                continue;
            }

            // 셸 확장 정규화 — `rm -rf $'/'`(ANSI-C quoting), `rm -rf ${HOME}`(파라미터 확장)은
            // bash 가 `/`, 홈으로 확장하지만 토큰 비교는 미스한다. ${X} → $X 로, $'x' → x 로 정규화.
            // (따옴표 Trim 보다 먼저 — Trim 이 끝따옴표를 먼저 깎으면 $'...' 폼이 무너진다.)
            if (token.StartsWith("$'", StringComparison.Ordinal) && token.EndsWith("'", StringComparison.Ordinal))
            {
                token = token[2..^1];
            }
            else if (token.StartsWith("${", StringComparison.Ordinal) && token.EndsWith("}", StringComparison.Ordinal))
            {
                token = "$" + token[2..^1];
            }

            // 따옴표 제거 — `rm -rf "/"` 같은 회피 시도.
            token = token.Trim('"', '\'');

            if (token.Length > 0)
            {
                targets.Add(token);
            }
        }

        return (found, recursive, force, targets);
    }

    // 셸 연산자를 구분자로도 취급해 `foo && rm -rf /` 를 놓치지 않는다.
    private static IEnumerable<string> Tokenize(string command)
    {
        var buf = new System.Text.StringBuilder();
        foreach (var c in command)
        {
            if (char.IsWhiteSpace(c) || c is ';' or '|' or '&')
            {
                if (buf.Length > 0)
                {
                    yield return buf.ToString();
                    buf.Clear();
                }

                continue;
            }

            buf.Append(c);
        }

        if (buf.Length > 0)
        {
            yield return buf.ToString();
        }
    }

    private static string Basename(string token)
    {
        var slash = token.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? token : token[(slash + 1)..];
    }
}
