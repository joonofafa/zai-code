using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Mcp.Skills;
using Xunit;

namespace MoaiCode.Core.Tests;

// 팀 공유 스킬: 서버 items[] → SKILL.md 기록 포맷이 SkillLoader 로 정확히 되읽히는지(계약)와
// 스킬 이름 → 디렉터리명 안전화(경로 탈출 방지)를 검증한다.
public class TeamSkillsTests
{
    [Fact]
    public void ToSkillMd_round_trips_through_SkillLoader()
    {
        var item = new TeamSkillItem("deploy-helper", "배포를 도와주는 스킬", "1단계: 빌드\n2단계: 배포");
        var dir = Path.Combine(Path.GetTempPath(), "moai-team-skills-test", Guid.NewGuid().ToString("N"));
        var skillDir = Path.Combine(dir, TeamSkills.SafeDirName(item.Name));
        Directory.CreateDirectory(skillDir);
        try
        {
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), TeamSkills.ToSkillMd(item));

            var loaded = SkillLoader.LoadFromDir(dir);
            var skill = Assert.Single(loaded);
            Assert.Equal("deploy-helper", skill.Name);
            Assert.Equal("배포를 도와주는 스킬", skill.Description);
            Assert.Equal("1단계: 빌드\n2단계: 배포", skill.Body.Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ToSkillMd_flattens_multiline_description()
    {
        var item = new TeamSkillItem("x", "첫 줄\n둘째 줄", "본문");
        var md = TeamSkills.ToSkillMd(item);
        var (meta, _) = FrontmatterParser.Parse(md);
        Assert.Equal("첫 줄 둘째 줄", meta["description"]); // 개행이 공백으로 평탄화되어 프론트매터가 깨지지 않음
    }

    [Theory]
    [InlineData("normal", "normal")]
    [InlineData("한글스킬", "한글스킬")]
    [InlineData("a/b", "a_b")]           // 경로 구분자 차단
    [InlineData("..", "")]               // 상위 경로 탈출 방지
    [InlineData("../etc", "_etc")]       // '/'→'_' 치환 후 선행 '.' 제거 (경로 탈출 불가)
    [InlineData("", "")]
    public void SafeDirName_blocks_path_escape(string input, string expected)
        => Assert.Equal(expected, TeamSkills.SafeDirName(input));

    [Fact]
    public void SkillState_round_trips_disabled_names_case_insensitively()
    {
        var file = Path.Combine(Path.GetTempPath(), $"moai-skills-disabled-{Guid.NewGuid():N}.json");
        try
        {
            // 파일 없음 → 빈 집합(전부 활성).
            Assert.Empty(MoaiCode.Mcp.Skills.SkillState.LoadDisabled(file));

            MoaiCode.Mcp.Skills.SkillState.SaveDisabled(new[] { "web-research", "pdf-filler" }, file);
            var loaded = MoaiCode.Mcp.Skills.SkillState.LoadDisabled(file);

            Assert.Equal(2, loaded.Count);
            Assert.Contains("web-research", loaded);
            Assert.Contains("PDF-FILLER", loaded); // 대소문자 무시로 매칭
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
