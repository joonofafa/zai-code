namespace MoaiCode.Core.Tools;

/// <summary>
/// 모든 엔진(하위 에이전트 포함)이 툴 실행이 끝날 때 알리는 지점. 감사 로그(Config.AuditLog)가 승인 기록에 결과(ok/error)를
/// 잇는 데 쓴다 — Core 는 Config 를 참조할 수 없어 CLI 시작 시 연결한다. 취소·예외도 실패로 알린다.
/// </summary>
public static class ToolEvents
{
    /// <summary>(tool_use_id, isError).</summary>
    public static Action<string, bool>? Completed { get; set; }
}
