using System.IO.Compression;
using System.Text.Json;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Mcp.Skills;
using MoaiCode.Tools.Agent;
using Xunit;

namespace MoaiCode.Core.Tests;

// 상시(passive) 스킬: 켜져 있으면 본문이 시스템 프롬프트에 늘 들어가고, 기본은 꺼짐, /skills 로 켜고 끈다.
public class PassiveSkillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mate-passive-" + Guid.NewGuid().ToString("n"));

    public PassiveSkillTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private Skill Write(string name, string frontmatter, string body)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        var f = Path.Combine(d, "SKILL.md");
        File.WriteAllText(f, $"---\nname: {name}\ndescription: d\n{frontmatter}---\n{body}\n");
        return SkillLoader.LoadFile(f, name)!;
    }

    [Fact]
    public void Passive_skill_starts_off_and_the_toggle_is_stored_apart_from_normal_skills()
    {
        var passive = Write("secure", "passive: true\n", "Use bound parameters.");
        var normal = Write("planner", "", "Make a plan.");
        Assert.True(passive.Passive);
        Assert.False(normal.Passive);

        var disabledFile = Path.Combine(_dir, "skills-disabled.json");
        var enabledFile = Path.Combine(_dir, "skills-enabled.json");
        bool Active(Skill s) => SkillState.IsActive(s, SkillState.LoadDisabled(disabledFile), SkillState.LoadEnabled(enabledFile));

        // 처음: 일반 스킬은 켜짐, 상시 스킬은 꺼짐.
        Assert.True(Active(normal));
        Assert.False(Active(passive));

        // /skills 에서 상시 스킬을 켜고 일반 스킬을 끔(피커는 꺼진 이름 목록을 돌려준다).
        SkillState.Save(new[] { passive, normal }, new[] { "planner" }, disabledFile, enabledFile);
        Assert.True(Active(passive));
        Assert.False(Active(normal));
        Assert.Equal(new[] { "planner" }, SkillState.LoadDisabled(disabledFile));
        Assert.Equal(new[] { "secure" }, SkillState.LoadEnabled(enabledFile));

        // 다시 둘 다 원래대로.
        SkillState.Save(new[] { passive, normal }, new[] { "secure" }, disabledFile, enabledFile);
        Assert.False(Active(passive));
        Assert.True(Active(normal));
    }

    [Fact]
    public void Turned_on_passive_skill_goes_into_the_system_prompt_with_its_folder()
    {
        var passive = Write("secure", "passive: true\n", "Read references/python.md for Python.");
        var section = SkillTool.AlwaysOnSection(new[] { passive });
        Assert.NotNull(section);
        Assert.Contains("# Always-on skills", section);
        Assert.Contains("## secure", section);
        Assert.Contains("Read references/python.md", section);
        Assert.Contains(Path.Combine(_dir, "secure"), section);   // references/ 를 찾을 수 있게 폴더 경로

        var ctx = new PromptContext { WorkingDirectory = _dir };
        Assert.Contains("Read references/python.md", SystemPromptBuilder.Build(ctx with { AlwaysOnSkills = section }));
        Assert.Null(SkillTool.AlwaysOnSection(Array.Empty<Skill>()));
        Assert.DoesNotContain("Always-on skills", SystemPromptBuilder.Build(ctx));
    }

    [Fact]
    public void Replacing_the_system_prompt_keeps_the_conversation()
    {
        var engine = new QueryEngine(new CaptureModel(), Array.Empty<ITool>());
        engine.Seed(new[] { new SystemMessage("OLD prompt") });
        engine.Restore(new List<Message> { new UserMessage("hello"), new AssistantMessage(new ContentBlock[] { new TextBlock("hi") }) });

        engine.ReplaceSeed(new[] { new SystemMessage("NEW prompt") });

        var system = Assert.Single(engine.Messages.OfType<SystemMessage>());
        Assert.Equal("NEW prompt", system.Text);
        Assert.Same(system, engine.Messages[0]);
        Assert.Equal(3, engine.Messages.Count);
        Assert.Contains(engine.Messages, m => m is UserMessage { Text: "hello" });

        engine.Reset();   // /clear 도 새 프롬프트로 돌아간다
        Assert.Equal("NEW prompt", Assert.IsType<SystemMessage>(Assert.Single(engine.Messages)).Text);
    }

    [Fact]
    public async Task Sub_agents_get_the_always_on_skills_at_call_time()
    {
        var model = new CaptureModel();
        string? extra = null;
        var agent = new AgentTool(model, Array.Empty<ITool>(), extraInstructions: () => extra);

        await Run(agent);
        Assert.DoesNotContain("Always-on", model.System);

        extra = "# Always-on skills\n\n## secure\n\nUse bound parameters.";   // /skills 로 켠 뒤
        await Run(agent);
        Assert.Contains("Use bound parameters.", model.System);
    }

    [Fact]
    public void Bundled_secure_coding_skill_is_passive()
    {
        using var stream = typeof(MoaiCode.Cli.BundledSkills).Assembly.GetManifestResourceStream("bundled-skills.zip");
        using var zip = new ZipArchive(stream!, ZipArchiveMode.Read);
        var path = Path.Combine(_dir, "SKILL.md");
        zip.GetEntry("secure-coding/SKILL.md")!.ExtractToFile(path);
        var skill = SkillLoader.LoadFile(path, "x")!;
        Assert.Equal("secure-coding", skill.Name);
        Assert.True(skill.Passive);
        foreach (var lang in new[] { "java-spring", "javascript-node", "python", "csharp-dotnet" })
        {
            Assert.NotNull(zip.GetEntry($"secure-coding/references/{lang}.md"));
        }
    }

    private static async Task Run(ITool tool)
    {
        using var doc = JsonDocument.Parse("""{"prompt":"write a query"}""");
        await foreach (var _ in tool.ExecuteAsync(doc.RootElement, new ToolContext(".", PermissionMode.Auto), default))
        {
        }
    }

    private sealed class CaptureModel : IChatModel
    {
        public string System { get; private set; } = "";

        public async IAsyncEnumerable<StreamEvent> StreamAsync(
            IReadOnlyList<Message> messages, IReadOnlyList<ITool> tools,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            System = messages.OfType<SystemMessage>().FirstOrDefault()?.Text ?? "";
            yield return new TextDelta("done");
            yield return new TurnCompleted(new Usage(1, 1), "stop");
        }
    }
}
