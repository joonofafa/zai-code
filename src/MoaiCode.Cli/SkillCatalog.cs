using MoaiCode.Mcp.Skills;

namespace MoaiCode.Cli;

/// <summary>
/// 모든 출처의 스킬을 원천(비활성 필터 전) 상태로 모은다 — 우선순위 user > plugin > team > bundled
/// (같은 이름은 앞선 것 유지). 시작 배선(AppBootstrap)·로그인 피커(LoginFlow)·/skills 가 공유한다.
/// 로컬 활성/비활성 필터는 호출측이 <see cref="SkillState"/> 로 별도 적용한다.
/// </summary>
public static class SkillCatalog
{
    public static List<(Skill Skill, string Source)> DiscoverAll(string cwd)
    {
        var all = new List<(Skill Skill, string Source)>();
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IEnumerable<Skill> src, string source)
        {
            foreach (var s in src)
            {
                if (have.Add(s.Name))
                {
                    all.Add((s, source));
                }
            }
        }

        Add(SkillLoader.Discover(cwd), "user");
        Add(PluginLoader.Discover(cwd), "plugin");
        Add(SkillLoader.LoadFromDir(BundledSkills.Dir), "bundled");
        return all;
    }
}
