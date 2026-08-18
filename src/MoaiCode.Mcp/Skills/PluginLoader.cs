namespace MoaiCode.Mcp.Skills;

/// <summary>
/// 플러그인 디스커버리 (TS plugins 대응, 스켈레톤). 플러그인 = 디렉토리(+선택적 plugin.json)이며
/// 현재는 플러그인의 skills/ 하위 스킬을 로드해 합류. 명령/MCP 기여는 후속.
/// 탐색 경로: ./.claude/plugins, ~/.claude/plugins, ~/.moai/plugins
/// </summary>
public static class PluginLoader
{
    public static IReadOnlyList<Skill> Discover(string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            Path.Combine(workingDirectory, ".claude", "plugins"),
            Path.Combine(home, ".claude", "plugins"),
            Path.Combine(home, ".zaicode", "plugins"),
        };

        var result = new List<Skill>();
        foreach (var pluginsRoot in dirs)
        {
            if (!Directory.Exists(pluginsRoot))
            {
                continue;
            }

            foreach (var pluginDir in Directory.GetDirectories(pluginsRoot))
            {
                var skillsDir = Path.Combine(pluginDir, "skills");
                if (Directory.Exists(skillsDir))
                {
                    result.AddRange(SkillLoader.LoadFromDir(skillsDir));
                }
            }
        }

        return result;
    }
}
