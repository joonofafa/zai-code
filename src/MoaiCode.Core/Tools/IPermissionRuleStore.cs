using MoaiCode.Core.Messages;

namespace MoaiCode.Core.Tools;

public enum RuleMatch { None, Allow, Deny }

/// <summary>
/// 영속 allow/deny 규칙 저장소(Claude Code 의 permissions.allow/deny 대응).
/// 규칙 추가는 즉시 메모리에 반영되고 settings.json 에 저장된다(다음 실행에도 유지).
/// </summary>
public interface IPermissionRuleStore
{
    IReadOnlyList<string> Allow { get; }
    IReadOnlyList<string> Deny { get; }

    /// <summary>이 호출에 대한 판정. deny 가 allow 를 이긴다.</summary>
    RuleMatch Evaluate(ITool tool, ToolUseBlock call);

    /// <summary>allow 규칙 추가(중복 무시) + 영속화.</summary>
    void AddAllow(string pattern);

    /// <summary>deny 규칙 추가(중복 무시) + 영속화.</summary>
    void AddDeny(string pattern);

    /// <summary>allow/deny 양쪽에서 패턴 제거 + 영속화. 제거된 게 있으면 true.</summary>
    bool Remove(string pattern);
}
