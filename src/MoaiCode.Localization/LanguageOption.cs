namespace MoaiCode.Localization;

/// <summary>UI에서 선택 가능한 언어. 표시명은 각 언어의 고유 표기를 사용한다.</summary>
public sealed record LanguageOption(string Code, string DisplayName);
