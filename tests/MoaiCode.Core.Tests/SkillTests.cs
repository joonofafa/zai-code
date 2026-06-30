using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp.Skills;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SkillTests : IDisposable
{
    private readonly string _dir;

    public SkillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-skill-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Frontmatter_parses_meta_and_body()
    {
        var content = "---\nname: deep-research\ndescription: Research a topic\n---\nDo the research steps.\n";
        var (meta, body) = FrontmatterParser.Parse(content);

        Assert.Equal("deep-research", meta["name"]);
        Assert.Equal("Research a topic", meta["description"]);
        Assert.Equal("Do the research steps.", body.Trim());
    }

    [Fact]
    public void Frontmatter_without_fence_returns_body_as_is()
    {
        var (meta, body) = FrontmatterParser.Parse("just a body");
        Assert.Empty(meta);
        Assert.Equal("just a body", body);
    }

    [Fact]
    public void Loader_finds_single_file_and_dir_skills()
    {
        File.WriteAllText(Path.Combine(_dir, "summarize.md"),
            "---\nname: summarize\ndescription: Summarize text\n---\nSummarize it.");

        var sub = Path.Combine(_dir, "refactor");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "SKILL.md"),
            "---\nname: refactor\ndescription: Refactor code\n---\nRefactor it.");

        var skills = SkillLoader.LoadFromDir(_dir);
        Assert.Equal(2, skills.Count);
        Assert.Contains(skills, s => s.Name == "summarize");
        Assert.Contains(skills, s => s.Name == "refactor");
    }

    [Fact]
    public async Task SkillTool_returns_skill_body()
    {
        var skills = new List<Skill>
        {
            new("planner", "Make a plan", "Step 1. Step 2.", "x"),
        };
        var tool = new SkillTool(skills);
        Assert.Contains("planner", tool.Description);

        using var doc = JsonDocument.Parse("""{"name":"planner"}""");
        var sb = "";
        var err = false;
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb += o.Text;
                err |= o.IsError;
            }
        }

        Assert.False(err);
        Assert.Contains("Step 1", sb);
    }

    [Fact]
    public async Task SkillTool_errors_on_unknown_skill()
    {
        var tool = new SkillTool(new List<Skill> { new("a", "", "body", "x") });
        using var doc = JsonDocument.Parse("""{"name":"missing"}""");
        var err = false;
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                err |= o.IsError;
            }
        }

        Assert.True(err);
    }
}
