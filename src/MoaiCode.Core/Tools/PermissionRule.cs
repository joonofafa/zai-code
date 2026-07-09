using System.Text.Json;
using MoaiCode.Core.Messages;

namespace MoaiCode.Core.Tools;

/// <summary>
/// "항상 허용"이 적용될 범위(스코프)를 계산한다.
/// 예전엔 툴 이름으로만 키를 잡아, 사용자가 `ls` 한 번에 항상 허용을 누르면 그 세션의 모든 Bash
/// 명령이 무조건 승인됐다. 이제 Bash 는 명령 prefix 단위(`Bash(git status)`)로 좁힌다.
/// </summary>
public static class PermissionRule
{
    /// <summary>서브커맨드까지 봐야 의미가 생기는 도구 (git status ≠ git push).</summary>
    private static readonly HashSet<string> SubcommandTools = new(StringComparer.Ordinal)
    {
        "git", "docker", "podman", "npm", "pnpm", "yarn", "dotnet", "kubectl", "cargo", "go",
        "uv", "pip", "pip3", "apt", "apt-get", "brew", "systemctl", "aws", "gcloud", "az",
        "terraform", "make", "zellij", "tmux",
    };

    /// <summary>한 번 허용해도 세션 전체로 넓히면 안 되는 명령 (매번 확인).</summary>
    private static readonly HashSet<string> NeverAlwaysAllow = new(StringComparer.Ordinal)
    {
        "rm", "rmdir", "dd", "mkfs", "chmod", "chown", "kill", "pkill", "killall",
        "sudo", "su", "doas", "env", "eval", "exec", "shutdown", "reboot",
        "ssh", "scp", "sftp", "rsync", "curl", "wget",
    };

    /// <summary>셸 연산자 — 있으면 첫 토큰이 명령 전체를 대표하지 못한다.</summary>
    private static readonly char[] ShellOperators = { ';', '|', '&', '`', '\n', '\r', '>', '<', '(', ')' };

    public static bool HasShellOperators(string command) =>
        command.IndexOfAny(ShellOperators) >= 0
        || command.Contains("$(", StringComparison.Ordinal)
        || command.Contains("${", StringComparison.Ordinal);

    /// <summary>
    /// 이 호출에 부여할 "항상 허용" 스코프. null 이면 항상 허용을 제공해선 안 된다.
    /// </summary>
    public static string? TryScope(ITool tool, ToolUseBlock call)
    {
        if (!string.Equals(tool.Name, "Bash", StringComparison.Ordinal))
        {
            return tool.Name;
        }

        var command = ReadCommand(call);
        if (string.IsNullOrWhiteSpace(command) || HasShellOperators(command))
        {
            return null; // 복합 명령은 prefix 로 대표할 수 없다 → 매번 확인
        }

        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return null;
        }

        var first = Basename(tokens[0]);
        if (first.Contains('=', StringComparison.Ordinal))
        {
            return null; // FOO=bar cmd — 환경 조작
        }

        if (NeverAlwaysAllow.Contains(first))
        {
            return null;
        }

        var prefix = SubcommandTools.Contains(first)
                     && tokens.Length > 1
                     && !tokens[1].StartsWith('-')
            ? $"{first} {Basename(tokens[1])}"
            : first;

        return $"Bash({prefix})";
    }

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
