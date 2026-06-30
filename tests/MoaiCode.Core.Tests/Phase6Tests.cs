using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp.Skills;
using MoaiCode.Providers;
using MoaiCode.Tools.Agent;
using MoaiCode.Tools.Tasks;
using Xunit;

namespace MoaiCode.Core.Tests;

public class AgentToolTests
{
    private static async Task<string> Run(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Sub_agent_runs_and_returns_text()
    {
        // EchoChatModel을 서브모델로 사용 → 서브에이전트가 프롬프트를 echo.
        var agent = new AgentTool(new EchoChatModel(), Array.Empty<ITool>());
        var output = await Run(agent, """{"prompt":"do the thing"}""");
        Assert.Contains("echo: do the thing", output);
    }

    [Fact]
    public async Task Missing_prompt_is_error()
    {
        var agent = new AgentTool(new EchoChatModel(), Array.Empty<ITool>());
        var output = await Run(agent, """{}""");
        Assert.Contains("required", output);
    }
}

public class TaskToolsTests
{
    private static async Task<string> Run(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Create_list_update_flow()
    {
        var store = new TaskStore();
        var create = new TaskCreateTool(store);
        var list = new TaskListTool(store);
        var update = new TaskUpdateTool(store);

        var created = await Run(create, """{"subject":"write tests"}""");
        Assert.Contains("created task #1", created);

        var listed = await Run(list, "{}");
        Assert.Contains("#1 [Pending] write tests", listed);

        var updated = await Run(update, """{"id":"1","status":"completed"}""");
        Assert.Contains("Completed", updated);

        Assert.Contains("[Completed]", await Run(list, "{}"));
    }

    [Fact]
    public async Task Update_unknown_task_errors()
    {
        var store = new TaskStore();
        var update = new TaskUpdateTool(store);
        Assert.Contains("not found", await Run(update, """{"id":"99","status":"done"}"""));
    }
}

public class PluginLoaderTests : IDisposable
{
    private readonly string _dir;

    public PluginLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-plugin-" + Guid.NewGuid().ToString("n"));
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
    public void Discovers_skills_from_plugin_skills_dir()
    {
        // {cwd}/.claude/plugins/myplugin/skills/foo.md
        var skillsDir = Path.Combine(_dir, ".claude", "plugins", "myplugin", "skills");
        Directory.CreateDirectory(skillsDir);
        File.WriteAllText(Path.Combine(skillsDir, "foo.md"),
            "---\nname: foo\ndescription: plugin skill\n---\nBody.");

        var skills = PluginLoader.Discover(_dir);
        Assert.Contains(skills, s => s.Name == "foo");
    }
}
