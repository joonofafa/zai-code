using System.Text.Json;
using MoaiCode.Core;

namespace MoaiCode.Mcp.Skills;

/// <summary>
/// 스킬 로컬 활성/비활성 상태(사용자가 /skills 로 끈 스킬 이름 집합)를 ~/.moai/skills-disabled.json 에
/// 보관한다. 기본은 '켜짐'(목록에 없으면 활성) — 새 스킬은 자동으로 활성. 팀 스킬 디렉터리는 sync 마다
/// 재생성되므로 상태를 스킬 파일에 담을 수 없어 별도 파일로 이름 기준 보관한다. 실패는 non-fatal.
/// 상시(passive) 스킬은 반대로 기본이 '꺼짐'이라, 사용자가 켠 이름을 skills-enabled.json 에 따로 보관한다.
/// </summary>
public static class SkillState
{
    private static string DefaultPath => AppPaths.Combine("skills-disabled.json");
    private static string DefaultEnabledPath => AppPaths.Combine("skills-enabled.json");

    /// <summary>켜진 상시(passive) 스킬 이름 집합. 파일 없음/파싱 실패는 빈 집합(모두 꺼짐).</summary>
    public static HashSet<string> LoadEnabled(string? path = null) => LoadDisabled(path ?? DefaultEnabledPath);

    /// <summary>켜진 상시(passive) 스킬 이름 집합을 저장한다.</summary>
    public static void SaveEnabled(IEnumerable<string> names, string? path = null) => SaveDisabled(names, path ?? DefaultEnabledPath);

    /// <summary>일반 스킬은 꺼지지 않았으면, 상시 스킬은 켜졌으면 활성.</summary>
    public static bool IsActive(Skill skill, ISet<string> disabled, ISet<string> enabled) =>
        skill.Passive ? enabled.Contains(skill.Name) : !disabled.Contains(skill.Name);

    /// <summary>/skills 피커 결과(꺼진 이름 목록)를 일반·상시 스킬별 파일에 나눠 저장한다.</summary>
    public static void Save(IEnumerable<Skill> all, IReadOnlyCollection<string> disabledNames,
        string? disabledPath = null, string? enabledPath = null)
    {
        var off = new HashSet<string>(disabledNames, StringComparer.OrdinalIgnoreCase);
        var skills = all.ToList();
        SaveDisabled(skills.Where(s => !s.Passive && off.Contains(s.Name)).Select(s => s.Name), disabledPath);
        SaveEnabled(skills.Where(s => s.Passive && !off.Contains(s.Name)).Select(s => s.Name), enabledPath);
    }

    /// <summary>비활성(꺼진) 스킬 이름 집합. 파일 없음/파싱 실패는 빈 집합(모두 활성).</summary>
    public static HashSet<string> LoadDisabled(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file));
            return new HashSet<string>(names ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 손상 시 전부 활성으로 취급(안전).
        }
    }

    /// <summary>비활성 스킬 이름 집합을 저장한다(중복 제거·정렬). 실패는 조용히 무시(non-fatal).</summary>
    public static void SaveDisabled(IEnumerable<string> names, string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var list = names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            File.WriteAllText(file, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 저장 실패는 치명적이지 않다 — 다음 세션에 이전 상태 유지.
        }
    }
}
