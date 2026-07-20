using System.Text.Json;
using MoaiCode.Core.Messages;

namespace MoaiCode.Core.Tools;

/// <summary>
/// 권한 규칙의 스코프 계산 + 패턴 매칭 (순수 로직).
/// 규칙 문자열은 Claude Code 형식을 따른다:
///   Bash(ssh moai-ec2)  — Bash 명령 prefix(토큰 단위) 매칭
///   Bash(git status)    — prefix
///   Write               — 툴 이름(모든 호출) 매칭
/// </summary>
public static class PermissionRule
{
    // 서브커맨드까지 봐야 의미가 생기는 도구 (git status ≠ git push).
    private static readonly HashSet<string> SubcommandTools = new(StringComparer.Ordinal)
    {
        "git", "docker", "podman", "npm", "pnpm", "yarn", "dotnet", "kubectl", "cargo", "go",
        "uv", "pip", "pip3", "apt", "apt-get", "brew", "systemctl", "aws", "gcloud", "az",
        "terraform", "make", "zellij", "tmux",
    };

    // 원격 접속 — 스코프에 대상 호스트를 포함한다(ssh moai-ec2). 호스트가 없으면 스코프 없음.
    private static readonly HashSet<string> HostCommands = new(StringComparer.Ordinal)
    {
        "ssh", "telnet", "mosh",
    };

    // 프롬프트의 "항상 허용"으로는 절대 넓히지 않는 파괴적/위험 명령 (손으로 settings 에 넣는 건 사용자 자유).
    // 크로스플랫폼(Win/Linux/macOS) — 대소문자 무시(PowerShell/cmd 는 대소문자 구분 안 함).
    private static readonly HashSet<string> NeverScope = new(StringComparer.OrdinalIgnoreCase)
    {
        // Unix / macOS
        "rm", "rmdir", "dd", "mkfs", "chmod", "chown", "kill", "pkill", "killall",
        "sudo", "su", "doas", "eval", "exec", "shutdown", "reboot", "curl", "wget", "diskutil",
        // Windows / PowerShell (파괴적)
        "del", "erase", "rd", "format", "diskpart", "cipher", "bcdedit", "fsutil",
        "takeown", "remove-item", "ri", "clear-disk", "format-volume", "reg",
    };

    private static readonly char[] ShellOperators = { ';', '|', '&', '`', '\n', '\r', '>', '<', '(', ')' };

    public static bool HasShellOperators(string command) =>
        command.IndexOfAny(ShellOperators) >= 0
        || command.Contains("$(", StringComparison.Ordinal)
        || command.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// 이 호출에 "항상 허용"으로 제공할 규칙 패턴(예: "Bash(ssh moai-ec2)"). null 이면 제공 금지.
    /// </summary>
    public static string? TryScope(ITool tool, ToolUseBlock call)
    {
        if (!string.Equals(tool.Name, "Bash", StringComparison.Ordinal))
        {
            return tool.Name;
        }

        var command = ReadCommand(call);
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        if (HasShellOperators(command))
        {
            // 복합 명령은 prefix 로 대표할 수 없다 → '이 명령 그대로'(정확 일치)로만 제공.
            // 단 어느 세그먼트든 NeverScope(rm/curl/sudo…) 면 항상 허용을 주지 않는다.
            return AnySegmentNeverScoped(command) ? null : "Bash(=" + command.Trim() + ")";
        }

        var tokens = Tokenize(command);
        if (tokens.Count == 0)
        {
            return null;
        }

        var first = Basename(tokens[0]);
        if (first.Contains('=', StringComparison.Ordinal) || NeverScope.Contains(first))
        {
            return null;
        }

        if (HostCommands.Contains(first))
        {
            var host = ExtractHost(tokens);
            return host is null ? null : $"Bash({first} {host})";
        }

        if (SubcommandTools.Contains(first) && tokens.Count > 1 && !tokens[1].StartsWith('-'))
        {
            return $"Bash({first} {Basename(tokens[1])})";
        }

        return $"Bash({first})";
    }

    /// <summary>규칙 패턴이 이 호출에 매치되면 true. allowMatch=true 면 복합 명령은 매치시키지 않는다.</summary>
    public static bool Matches(string pattern, ITool tool, ToolUseBlock call, bool allowMatch)
    {
        var (patTool, patInner) = ParsePattern(pattern);
        if (!string.Equals(patTool, tool.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (patInner is null)
        {
            return true; // 툴 이름 규칙 — 모든 호출
        }

        if (!string.Equals(tool.Name, "Bash", StringComparison.Ordinal))
        {
            return false;
        }

        var command = ReadCommand(call);
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var patTokens = Tokenize(patInner);
        if (patTokens.Count == 0)
        {
            return false;
        }

        // 허용 매치.
        if (allowMatch)
        {
            // 정확 일치 규칙 Bash(=<cmd>): 복합 명령이라도 바이트 동일할 때만 매치.
            if (patInner.StartsWith("=", StringComparison.Ordinal))
            {
                return string.Equals(command.Trim(), patInner[1..].Trim(), StringComparison.Ordinal);
            }

            // 그 외: 복합 명령이면 첫 세그먼트가 명령 전체를 대표하지 못하므로 매치 금지.
            return !HasShellOperators(command) && SegmentMatches(command, patTokens);
        }

        // 거부 매치: 어떤 세그먼트든 걸리면 막는다(foo && curl evil).
        return SplitSegments(command).Any(seg => SegmentMatches(seg, patTokens));
    }

    private static bool SegmentMatches(string segment, IReadOnlyList<string> patTokens)
    {
        var tokens = Tokenize(segment);
        if (tokens.Count == 0 || patTokens.Count == 0)
        {
            return false;
        }

        // 첫 토큰은 basename 비교(/usr/bin/ssh ↔ ssh).
        if (!string.Equals(Basename(tokens[0]), patTokens[0], StringComparison.Ordinal))
        {
            return false;
        }

        // 원격 명령은 플래그(-o 등) 때문에 호스트 위치가 밀린다 → 위치가 아니라 호스트를 추출해 비교.
        // (TryScope 가 호스트 인지 스코프를 만드는 것과 대칭이어야 매칭이 성립한다.)
        if (patTokens.Count == 2 && HostCommands.Contains(patTokens[0]))
        {
            return string.Equals(ExtractHost(tokens), patTokens[1], StringComparison.Ordinal);
        }

        if (tokens.Count < patTokens.Count)
        {
            return false;
        }

        for (var i = 1; i < patTokens.Count; i++)
        {
            if (!string.Equals(tokens[i], patTokens[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"Bash(ssh moai-ec2)" → ("Bash","ssh moai-ec2"), "Write" → ("Write", null).</summary>
    public static (string Tool, string? Inner) ParsePattern(string pattern)
    {
        var p = pattern.Trim();
        var open = p.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !p.EndsWith(')'))
        {
            return (p, null);
        }

        var tool = p[..open].Trim();
        var inner = p[(open + 1)..^1].Trim();
        // Claude Code 형식 "Bash(ssh moai-ec2:*)" 의 후행 :* 를 허용.
        if (inner.EndsWith(":*", StringComparison.Ordinal))
        {
            inner = inner[..^2];
        }

        return (tool, inner.Length == 0 ? null : inner);
    }

    // 복합 명령에 NeverScope(rm/curl/sudo…) 토큰이 하나라도 있으면 true → 정확일치 항상허용도 금지.
    // (ls | xargs rm 처럼 첫 토큰이 아닌 위치의 위험 명령도 잡기 위해 세그먼트 내 전체 토큰을 본다.)
    private static bool AnySegmentNeverScoped(string command)
    {
        foreach (var seg in SplitSegments(command))
        {
            foreach (var tok in Tokenize(seg))
            {
                if (NeverScope.Contains(Basename(tok)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> SplitSegments(string command)
    {
        var seg = new System.Text.StringBuilder();
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c is ';' or '|' or '&' or '\n' or '\r' or '(' or ')' or '`')
            {
                if (seg.Length > 0) { yield return seg.ToString(); seg.Clear(); }
            }
            else
            {
                seg.Append(c);
            }
        }

        if (seg.Length > 0)
        {
            yield return seg.ToString();
        }
    }

    // ssh 에서 값을 갖는 단문자 플래그(-p 22, -i key…). 그 뒤 토큰은 호스트가 아니다.
    private const string SshValueFlags = "picoFlbDEeIJLmORSWw";

    private static string? ExtractHost(IReadOnlyList<string> tokens)
    {
        for (var i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.StartsWith('-'))
            {
                // "-p" 처럼 정확히 -X 이고 값-플래그면 다음 토큰(값)을 건너뛴다. "-p22" 는 자체로 소비됨.
                if (t.Length == 2 && SshValueFlags.IndexOf(t[1]) >= 0)
                {
                    i++;
                }

                continue;
            }

            // user@host → host 만 스코프에 쓴다.
            var at = t.IndexOf('@');
            return at >= 0 ? t[(at + 1)..] : t;
        }

        return null;
    }

    private static List<string> Tokenize(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
         .Select(t => t.Trim('"', '\'')).Where(t => t.Length > 0).ToList();

    private static string? ReadCommand(ToolUseBlock call)
    {
        if (call.Input.ValueKind != JsonValueKind.Object ||
            !call.Input.TryGetProperty("command", out var ce) ||
            ce.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return ce.GetString();
    }

    private static string Basename(string token)
    {
        var slash = token.LastIndexOfAny(new[] { '/', '\\' });
        return slash < 0 ? token : token[(slash + 1)..];
    }
}
