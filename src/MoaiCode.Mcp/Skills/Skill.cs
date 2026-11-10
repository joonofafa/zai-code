namespace MoaiCode.Mcp.Skills;

/// <summary>
/// 스킬 = 프론트매터(name/description/license) + 본문(지침 프롬프트). License는 선택 메타.
/// Passive(<c>passive: true</c>)=켜져 있으면 본문을 시스템 프롬프트에 상시 넣는 스킬. 기본은 꺼짐(/skills 로 켬).
/// </summary>
public sealed record Skill(string Name, string Description, string Body, string Path, string? License = null, bool Passive = false);
