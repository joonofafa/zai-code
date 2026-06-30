using System.Text;
using System.Text.Json;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Agent;
using MoaiCode.Tools.Files;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

public class OutputStyleTests
{
    [Fact]
    public void Maps_styles_and_injects_into_system_prompt()
    {
        Assert.Contains("★ Insight", OutputStyles.PromptFor("explanatory"));
        Assert.Contains("Learn by Doing", OutputStyles.PromptFor("learning"));
        Assert.Null(OutputStyles.PromptFor(null));

        var p = SystemPromptBuilder.Build(new PromptContext
        {
            WorkingDirectory = "/x",
            OutputStyle = "explanatory",
        });
        Assert.Contains("Output Style: Explanatory", p);
    }
}

public class ReadBeforeEditTests : IDisposable
{
    private readonly string _dir;
    private readonly ReadTracker _reads = new();

    public ReadBeforeEditTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-rbe-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "alpha beta");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<(string Output, bool IsError)> Run(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        var err = false;
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, new ToolContext(_dir, PermissionMode.Auto, _reads), default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
                err |= o.IsError;
            }
        }

        return (sb.ToString(), err);
    }

    [Fact]
    public async Task Edit_without_prior_read_is_blocked()
    {
        var (output, err) = await Run(new FileEditTool(),
            """{"path":"f.txt","old_string":"alpha","new_string":"gamma"}""");
        Assert.True(err);
        Assert.Contains("Read tool", output);
    }

    [Fact]
    public async Task Edit_after_read_succeeds()
    {
        await Run(new FileReadTool(), """{"path":"f.txt"}""");
        var (_, err) = await Run(new FileEditTool(),
            """{"path":"f.txt","old_string":"alpha","new_string":"gamma"}""");
        Assert.False(err);
        Assert.Contains("gamma", File.ReadAllText(Path.Combine(_dir, "f.txt")));
    }
}

public class VerificationAndSlashTests
{
    [Fact]
    public void Verification_subagent_type_maps()
    {
        Assert.Contains("try to break it", SubAgentPrompts.ForType("verification"));
        Assert.Contains("VERDICT", SubAgentPrompts.ForType("verify"));
    }

    [Fact]
    public void Init_and_review_are_prompt_commands()
    {
        var reg = SlashRegistry.CreateDefault();
        Assert.True(reg.TryGet("init", out _));
        Assert.True(reg.TryGet("review", out _));
    }
}
