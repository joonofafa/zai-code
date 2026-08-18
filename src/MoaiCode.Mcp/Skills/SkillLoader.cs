namespace MoaiCode.Mcp.Skills;

/// <summary>
/// 스킬 디스커버리. 지원 구조:
///  - {skillsDir}/{name}/SKILL.md  (Claude Code 스타일)
///  - {skillsDir}/{name}.md        (단일 파일)
/// 탐색 경로: ./.claude/skills, ~/.claude/skills, ~/.moai/skills
/// </summary>
public static class SkillLoader
{
    public static IReadOnlyList<Skill> Discover(string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            Path.Combine(workingDirectory, ".claude", "skills"),
            Path.Combine(home, ".claude", "skills"),
            Path.Combine(home, ".zaicode", "skills"),
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Skill>();
        foreach (var dir in dirs)
        {
            foreach (var skill in LoadFromDir(dir))
            {
                if (seen.Add(skill.Name))
                {
                    result.Add(skill);
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<Skill> LoadFromDir(string dir)
    {
        var result = new List<Skill>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        // {name}/SKILL.md
        foreach (var sub in Directory.GetDirectories(dir))
        {
            var skillFile = Path.Combine(sub, "SKILL.md");
            if (File.Exists(skillFile))
            {
                var s = LoadFile(skillFile, fallbackName: Path.GetFileName(sub));
                if (s is not null)
                {
                    result.Add(s);
                }
            }
        }

        // {name}.md
        foreach (var file in Directory.GetFiles(dir, "*.md"))
        {
            var s = LoadFile(file, fallbackName: Path.GetFileNameWithoutExtension(file));
            if (s is not null)
            {
                result.Add(s);
            }
        }

        return result;
    }

    public static Skill? LoadFile(string path, string fallbackName)
    {
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }

        var (meta, body) = FrontmatterParser.Parse(content);
        var name = meta.TryGetValue("name", out var n) && n.Length > 0 ? n : fallbackName;
        var desc = meta.TryGetValue("description", out var d) ? d : "";
        return new Skill(name, desc, body, path);
    }
}
