using System.Text.Json;
using MoaiCode.Cli;
using MoaiCode.Config;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Security;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Bash;
using MoaiCode.Tui.Commands;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>격리 실행(bubblewrap) — 사람이 승인하지 않은 셸 명령은 작업 폴더 밖을 바꿀 수 없다.</summary>
[Collection("EnvMutating")]
public sealed class SandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mate-sbx-" + Guid.NewGuid().ToString("n"));
    private readonly string _workspace;
    private readonly string _privateTmp;
    // 격리 안에서 쓰면 안 되는 바깥 폴더 — /tmp 는 격리 안에서 전용 폴더로 바뀌므로 테스트 출력 폴더 아래에 둔다.
    private readonly string _outside = Path.Combine(AppContext.BaseDirectory, "sbx-outside-" + Guid.NewGuid().ToString("n"));
    private readonly string _audit = Path.Combine(Path.GetTempPath(), "mate-sbx-audit-" + Guid.NewGuid().ToString("n") + ".jsonl");
    private readonly string _previousAudit = AuditLog.FilePath;

    public SandboxTests()
    {
        _workspace = Path.Combine(_root, "ws");
        _privateTmp = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(_workspace);
        Directory.CreateDirectory(_privateTmp);
        Directory.CreateDirectory(_outside);
        SandboxPolicy.Mode = SandboxMode.Auto;
        SandboxPolicy.Available = Sandbox.Probe();
        SandboxPolicy.Writable = [_workspace];
        SandboxPolicy.TmpDir = _privateTmp;
        AuditLog.FilePath = _audit;
    }

    public void Dispose()
    {
        SandboxPolicy.Mode = SandboxMode.Off;
        SandboxPolicy.Available = false;
        SandboxPolicy.Writable = [];
        SandboxPolicy.TmpDir = null;
        AuditLog.FilePath = _previousAudit;
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
        File.Delete(_audit);
    }

    private static async Task<(string Text, bool Error)> Run(string command, string cwd, string? id = null)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));
        var tool = new BashTool();
        var ctx = new ToolContext(cwd, PermissionMode.Auto, ToolUseId: id);
        var text = "";
        var error = false;
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, ctx, default))
        {
            if (p is ToolOutput o)
            {
                text += o.Text;
                error |= o.IsError;
            }
        }

        return (text, error);
    }

    [Fact]
    public async Task Sandboxed_commands_can_only_write_the_workspace_and_cannot_reach_host_sockets()
    {
        if (!SandboxPolicy.Available)
        {
            return;   // bwrap 이 없는 환경(Windows·컨테이너)에서는 확인할 것이 없다
        }

        var ok = await Run("echo hi > made.txt && cat made.txt", _workspace);
        Assert.False(ok.Error, ok.Text);
        Assert.True(File.Exists(Path.Combine(_workspace, "made.txt")), ok.Text);

        var outside = Path.Combine(_outside, "x.txt");
        var blocked = await Run($"touch '{outside}'", _workspace);
        Assert.True(blocked.Error, blocked.Text);
        Assert.False(File.Exists(outside));
        Assert.Contains("dangerously_disable_sandbox", blocked.Text);   // 모델에게 주는 안내

        var marker = Path.Combine(Path.GetTempPath(), "mate-host-tmp-" + Guid.NewGuid().ToString("n"));
        File.WriteAllText(marker, "x");
        try
        {
            var tmp = await Run($"ls '{marker}' 2>&1; ls /run; test -e /var/run/docker.sock && echo SOCK || echo nosock; ls /proc | grep -c '^[0-9]'", _workspace);
            Assert.DoesNotContain(marker + "\n", tmp.Text.Replace("\r", ""));   // 호스트 /tmp 파일이 보이지 않는다
            Assert.Contains("nosock", tmp.Text);
            var pids = int.Parse(tmp.Text.Trim().Split('\n').Last());
            Assert.True(pids < 10, $"host processes visible: {pids}");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task Calls_the_gate_marked_as_approved_run_on_the_host()
    {
        if (!SandboxPolicy.Available)
        {
            return;
        }

        var outside = Path.Combine(_outside, "y.txt");
        SandboxPolicy.AllowUnsandboxed("approved-1");
        var r = await Run($"touch '{outside}'", _workspace, "approved-1");
        Assert.False(r.Error, r.Text);
        Assert.True(File.Exists(outside));
    }

    // ---------- 게이트 ----------

    private sealed class FakeTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "";
        public bool IsReadOnly => false;
        public bool IsConcurrencySafe => true;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public IAsyncEnumerable<ToolProgress> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class Human(bool answer) : IPermissionGate, ICriticalConfirmGate
    {
        public int Asked { get; private set; }

        public ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
        {
            Asked++;
            return ValueTask.FromResult(answer);
        }

        public ValueTask<bool> ConfirmCriticalAsync(ITool tool, ToolUseBlock call, string reason, CancellationToken ct)
        {
            Asked++;
            return ValueTask.FromResult(answer);
        }
    }

    private sealed class AllowAll : IRiskClassifier
    {
        public ValueTask<RiskVerdict?> ClassifyAsync(string toolName, string commandOrArgs, string? userRequest, CancellationToken ct) =>
            ValueTask.FromResult<RiskVerdict?>(new RiskVerdict(RiskDecision.Allow, "ok"));
    }

    private static ToolUseBlock Call(string id, object input)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(input));
        return new ToolUseBlock(id, "Bash", doc.RootElement.Clone());
    }

    private ModeAwarePermissionGate Gate(AgentMode mode, IPermissionGate inner, IPermissionGate? confirmer, bool ignoreConfirm = false) =>
        new(new AgentRuntimeState { Mode = mode }, inner, _workspace, confine: false, confirmer: confirmer,
            classifier: new AllowAll(), rules: new PermissionRules(), ignoreConfirm: ignoreConfirm);

    [Fact]
    public async Task Only_calls_a_person_approved_leave_the_sandbox()
    {
        SandboxPolicy.Available = true;   // 게이트 판정만 본다

        var auto = Gate(AgentMode.AutoAct, new AutoApproveGate(), new Human(true));
        Assert.True(await auto.AllowAsync(new FakeTool("Bash"), Call("a1", new { command = "npm run build" }), default));
        Assert.False(SandboxPolicy.TakeUnsandboxed("a1"));   // 자동 승인 → 격리

        var human = new Human(true);
        var act = Gate(AgentMode.Act, human, human);
        Assert.True(await act.AllowAsync(new FakeTool("Bash"), Call("a2", new { command = "npm run build" }), default));
        Assert.True(SandboxPolicy.TakeUnsandboxed("a2"));    // 사람이 이 호출을 승인 → 호스트

        var batch = Gate(AgentMode.Act, new AutoApproveGate(), null, ignoreConfirm: true);
        Assert.True(await batch.AllowAsync(new FakeTool("Bash"), Call("a3", new { command = "npm run build" }), default));
        Assert.True(SandboxPolicy.TakeUnsandboxed("a3"));    // 배치(--ignore-confirm)는 실제 변경을 위해 호스트
    }

    [Fact]
    public async Task Leaving_the_sandbox_needs_a_person_and_required_mode_fails_closed()
    {
        SandboxPolicy.Available = true;
        var human = new Human(false);
        var gate = Gate(AgentMode.AutoAct, new AutoApproveGate(), human);
        Assert.False(await gate.AllowAsync(new FakeTool("Bash"),
            Call("b1", new { command = "touch /etc/x", dangerously_disable_sandbox = true }), default));
        Assert.Equal(1, human.Asked);

        // 필수인데 쓸 수 없다: 사람이 없으면 자동 승인 명령도 거부, 있으면 묻는다.
        SandboxPolicy.Mode = SandboxMode.Required;
        SandboxPolicy.Available = false;
        var headless = Gate(AgentMode.AutoAct, new AutoApproveGate(), null);
        Assert.False(await headless.AllowAsync(new FakeTool("Bash"), Call("b2", new { command = "npm run build" }), default));

        var asked = new Human(true);
        var interactive = Gate(AgentMode.AutoAct, new AutoApproveGate(), asked);
        Assert.True(await interactive.AllowAsync(new FakeTool("Bash"), Call("b3", new { command = "npm run build" }), default));
        Assert.Equal(1, asked.Asked);
    }

    [Fact]
    public void Sandbox_and_writable_paths_come_only_from_the_user_layer()
    {
        var user = SettingsLoader.ApplyJson(Settings.Default, """{"sandbox":"required","sandboxWritable":["/srv/data"]}""");
        Assert.Equal("required", user.Sandbox);
        Assert.Equal(["/srv/data"], user.SandboxWritable);

        var project = SettingsLoader.ApplyJson(user, """{"sandbox":"off","sandboxWritable":["/"]}""",
            allowSensitive: false, allowAutomation: false, allowPermissionRules: false);
        Assert.Equal("required", project.Sandbox);
        Assert.Equal(["/srv/data"], project.SandboxWritable);
        Assert.Equal("off", SettingsLoader.ApplyJson(Settings.Default, """{"sandbox":false}""").Sandbox);
    }
}
