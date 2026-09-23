namespace MoaiCode.Mcp.Skills;

/// <summary>스킬 = 프론트매터(name/description/license) + 본문(지침 프롬프트). License는 선택 메타.</summary>
public sealed record Skill(string Name, string Description, string Body, string Path, string? License = null);
