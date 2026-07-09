namespace MoaiCode.Core.Security;

public enum RiskDecision
{
    /// <summary>맥락상 명백히 안전 — 자동 승인해도 된다.</summary>
    Allow,

    /// <summary>사람이 한 번 봐야 한다.</summary>
    Confirm,

    /// <summary>맥락상 요청되지 않았거나 파괴적 — 거부.</summary>
    Deny,
}

public sealed record RiskVerdict(RiskDecision Decision, string Reason);

/// <summary>
/// 규칙(deny-list)이 잡지 못하는 위험을 대화 맥락으로 판정한다.
/// 규칙은 누군가 미리 적어둔 패턴만 잡지만, "에이전트가 만들지 않은 리소스를 지운다" 같은 판단은
/// 명령 문자열이 아니라 맥락에 있다.
/// </summary>
public interface IRiskClassifier
{
    /// <summary>판정. 호출 실패/파싱 실패 시 null (호출측이 fail-closed 로 처리).</summary>
    ValueTask<RiskVerdict?> ClassifyAsync(
        string toolName, string commandOrArgs, string? userRequest, CancellationToken ct);
}
