using MoaiCode.Localization;
using Xunit;

namespace MoaiCode.Core.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("ko", "ko")]
    [InlineData("ko-KR", "ko")]
    [InlineData("EN_us", "en")]
    [InlineData("ja", null)]
    [InlineData("", null)]
    public void NormalizeLanguage_accepts_supported_language_and_region_codes(string input, string? expected)
        => Assert.Equal(expected, L10n.NormalizeLanguage(input));

    [Fact]
    public void Catalogs_return_korean_and_english_without_changing_global_state()
    {
        Assert.Equal("설정", L10n.GetForLanguage("ko", "gui.settings.title"));
        Assert.Equal("Settings", L10n.GetForLanguage("en-US", "gui.settings.title"));
        Assert.Equal("Saved: TEST_API_KEY",
            L10n.GetForLanguage("en", "cli.auth.saved", "TEST_API_KEY"));
    }

    [Fact]
    public void New_skill_keys_localize_per_language_with_args()
    {
        // 팀 스킬/스킬 피커 신규 문자열이 ko/en 각각으로 전환되고 자리표시가 채워지는지.
        Assert.Contains("활성 스킬", L10n.GetForLanguage("ko", "slash.skills.synced", 3, 5));
        Assert.Contains("active skills", L10n.GetForLanguage("en", "slash.skills.synced", 3, 5));
        Assert.Equal("skills: 2 enabled · 1 disabled (saved).",
            L10n.GetForLanguage("en", "slash.skills.toggleSaved", 2, 1));
        Assert.Contains("Space", L10n.GetForLanguage("en", "slash.skills.pickerTitle"));
        Assert.Contains("토글", L10n.GetForLanguage("ko", "slash.skills.pickerTitle"));
    }

    [Fact]
    public void Missing_key_falls_back_to_key()
        => Assert.Equal("missing.test.key", L10n.GetForLanguage("ko", "missing.test.key"));

    [Fact]
    public void Supported_languages_have_stable_codes()
        => Assert.Equal(new[] { "ko", "en" }, L10n.SupportedLanguages.Select(x => x.Code));

    [Fact]
    public void Every_catalog_has_the_same_keys()
        => Assert.Equal(
            L10n.GetKeys("ko").OrderBy(x => x),
            L10n.GetKeys("en").OrderBy(x => x));
}
