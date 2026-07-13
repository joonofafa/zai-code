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

        return RuleMatch.None;
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
