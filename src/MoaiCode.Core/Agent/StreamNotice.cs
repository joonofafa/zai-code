namespace MoaiCode.Core.Agent;

/// <summary>
/// 비-콘텐츠 상태 안내 이벤트(예: 스트림 중간 끊김 후 재시도). assistantText 에 누적되지 않으며
/// 대화 히스토리/세션에도 저장되지 않는다 — 순수하게 사용자에게 표시하기 위한 일시적 이벤트.
/// </summary>
public sealed record StreamNotice(string Text) : StreamEvent;
