using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Persistence;
using MoaiCode.Providers;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

public class SlashCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly QueryEngine _engine;
    private readonly SlashContext _ctx;
    private readonly SlashRegistry _reg;

    public SlashCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "occs-slash-" + Guid.NewGuid().ToString("n"));
        _engine = new QueryEngine(new EchoChatModel(), Array.Empty<ITool>());
        _engine.Seed(new[] { new SystemMessage("sys") });
        _ctx = new SlashContext(
            _engine,
            new SessionStore(_dir),
            new HistoryStore(Path.Combine(_dir, "history.jsonl")),
            new CheckpointStore(_dir, Path.Combine(_dir, "checkpoints")),
            new AgentRuntimeState(),
            new[] { "Read", "Bash" },
            new[] { "greet" },
            new[] { "py" },
            "EchoChatModel");
        _reg = SlashRegistry.CreateDefault();
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

    private async Task<SlashResult> Run(string name, params string[] args)
    {
        Assert.True(_reg.TryGet(name, out var cmd), $"command not found: {name}");
        return await cmd.ExecuteAsync(_ctx, args, default);
    }

    [Fact]
    public async Task Help_lists_commands()
    {
        var r = await Run("help");
        Assert.Contains("/tools", r.Output);
        Assert.Contains("/save", r.Output);
    }

    [Fact]
    public async Task Tools_and_model_and_skills_and_mcp_report_context()
    {
        Assert.Contains("Read", (await Run("tools")).Output);
        Assert.Contains("EchoChatModel", (await Run("model")).Output);
        Assert.Contains("greet", (await Run("skills")).Output);
        Assert.Contains("py", (await Run("mcp")).Output);
    }

    [Fact]
    public void Exit_and_quit_alias_resolve()
    {
        Assert.True(_reg.TryGet("exit", out _));
        Assert.True(_reg.TryGet("quit", out _));
    }

    [Fact]
    public async Task Exit_signals_quit()
    {
        Assert.True((await Run("exit")).Quit);
    }

    [Fact]
    public async Task Plan_and_act_switch_mode()
    {
        Assert.Equal(AgentMode.Act, _ctx.State.Mode);
        Assert.Contains("plan", (await Run("plan")).Output);
        Assert.Equal(AgentMode.Plan, _ctx.State.Mode);
        Assert.Contains("act", (await Run("act")).Output);
        Assert.Equal(AgentMode.Act, _ctx.State.Mode);
    }

    [Fact]
    public async Task Save_then_resume_roundtrips_conversation()
    {
        // 대화 1턴 진행 → 메시지 누적 (system, user, assistant)
        await foreach (var _ in _engine.SubmitAsync("hi"))
        {
        }

        var beforeCount = _engine.Messages.Count;
        Assert.True(beforeCount >= 3);

        var save = await Run("save", "mysession");
        Assert.Contains("저장됨", save.Output);

        await Run("clear"); // 메시지를 seed(system 1개)로 초기화
        Assert.Single(_engine.Messages);

        var resume = await Run("resume", "mysession");
        Assert.Contains("복원됨", resume.Output);
        Assert.Equal(beforeCount, _engine.Messages.Count);
    }

    [Fact]
    public async Task Unknown_session_resume_reports_missing()
    {
        var r = await Run("resume", "nope");
        Assert.Contains("없음", r.Output);
    }
}
