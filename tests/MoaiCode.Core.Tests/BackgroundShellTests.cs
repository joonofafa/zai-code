using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Bash;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>Bash(run_in_background) → BashOutput → KillShell 왕복. Unix 셸 전제(Windows 는 실기 검증).</summary>
// 셸을 실제로 실행한다 — 격리 실행 설정(SandboxPolicy, 전역)을 바꾸는 SandboxTests 와 동시에 돌지 않게.
[Collection("EnvMutating")]
public class BackgroundShellTests
{
    private static async Task<string> Run(ITool tool, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        var ctx = new ToolContext(Path.GetTempPath(), PermissionMode.Auto);
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, ctx, default))
        {
            if (p is ToolOutput o)
            {
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    private static async Task WaitUntil(Func<bool> cond, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!cond() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(cond(), "condition not met within timeout");
    }

    [Fact]
    public async Task Background_returns_immediately_and_output_is_incremental()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var bash = new BashTool(reg);
        var outTool = new BashOutputTool(reg);

        var started = await Run(bash, """{"command":"echo one; sleep 0.3; echo two; sleep 30; echo three","run_in_background":true}""");
        Assert.Contains("bash_1", started);        // 즉시 반환(sleep 30 을 기다리지 않음)

        var shell = reg.Get("bash_1")!;
        await WaitUntil(() => shell.UnreadLength >= "one\ntwo\n".Length);

        var first = await Run(outTool, """{"shell_id":"bash_1"}""");
        Assert.Contains("one", first);
        Assert.Contains("two", first);
        Assert.Contains("running", first, StringComparison.OrdinalIgnoreCase);

        var second = await Run(outTool, """{"shell_id":"bash_1"}""");
        Assert.DoesNotContain("one", second);     // 증분: 이미 읽은 건 다시 안 옴
        Assert.DoesNotContain("two", second);

        var killed = await Run(new KillShellTool(reg), """{"shell_id":"bash_1"}""");
        Assert.Contains("bash_1", killed);
        await shell.Completion;
        Assert.Equal(BackgroundShellStatus.Killed, shell.Status);

        var after = await Run(outTool, """{"shell_id":"bash_1"}""");
        Assert.Contains("killed", after, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("three", after);    // kill 됐으니 sleep 뒤 echo 는 안 나옴
    }

    [Fact]
    public async Task Completed_shell_reports_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var shell = reg.Start("echo done; exit 3", Path.GetTempPath());
        await shell.Completion;

        Assert.Equal(BackgroundShellStatus.Completed, shell.Status);
        Assert.Equal(3, shell.ExitCode);

        var text = await Run(new BashOutputTool(reg), """{"shell_id":"bash_1"}""");
        Assert.Contains("done", text);
        Assert.Contains("exit code 3", text);

        var kill = await Run(new KillShellTool(reg), """{"shell_id":"bash_1"}""");
        Assert.Contains("not running", kill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit code 3", kill);    // 이미 끝난 셸의 상태를 그대로 알려줌
    }

    [Fact]
    public async Task Filter_keeps_only_matching_lines()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var shell = reg.Start("printf 'INFO a\\nERROR b\\nINFO c\\n'", Path.GetTempPath());
        await shell.Completion;

        var text = await Run(new BashOutputTool(reg), """{"shell_id":"bash_1","filter":"ERROR"}""");
        Assert.Contains("ERROR b", text);
        Assert.DoesNotContain("INFO", text);
    }

    [Fact]
    public async Task Buffer_cap_keeps_tail_and_flags_truncation()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry(maxChars: 50);
        var shell = reg.Start("for i in $(seq 1 40); do echo line$i; done", Path.GetTempPath());
        await shell.Completion;

        var (text, truncated, _, _) = shell.ReadNew();
        Assert.True(truncated);
        Assert.True(text.Length <= 50);
        Assert.Contains("line40", text);
        Assert.DoesNotContain("line1\n", text);
    }

    [Fact]
    public async Task Unknown_shell_id_is_an_error()
    {
        var reg = new BackgroundShellRegistry();
        var text = await Run(new BashOutputTool(reg), """{"shell_id":"bash_99"}""");
        Assert.Contains("bash_99", text);
    }

    [Fact]
    public async Task Sync_bash_is_unchanged_when_flag_is_false()
    {
        if (OperatingSystem.IsWindows()) return;

        var text = await Run(new BashTool(new BackgroundShellRegistry()), """{"command":"echo sync","run_in_background":false}""");
        Assert.Equal("sync", text.Trim());
    }

    [Fact]
    public async Task Completion_notification_carries_shell_id_status_and_exit_code()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var received = new List<BackgroundShellFinished>();
        using var gate = new SemaphoreSlim(0);
        // 통보는 셸마다 다른 스레드풀 스레드에서 동시에 온다 — List 는 동시 Add 에 안전하지 않아 가끔 한 건이
        // 사라졌다(간헐 실패의 원인). 잠금으로 직렬화한다.
        reg.OnShellFinished = f =>
        {
            lock (received)
            {
                received.Add(f);
            }

            gate.Release();
        };

        // 1) 정상 종료(exit 0)
        var ok = reg.Start("echo ok", Path.GetTempPath());
        // 2) 비정상 종료(exit 7)
        var bad = reg.Start("exit 7", Path.GetTempPath());

        for (var i = 0; i < 2; i++)
        {
            await gate.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await Task.WhenAll(ok.Completion, bad.Completion);

        Assert.Equal(2, received.Count);
        var okNote = Assert.Single(received, f => f.Id == ok.Id);
        Assert.Equal(BackgroundShellStatus.Completed, okNote.Status);
        Assert.Equal(0, okNote.ExitCode);
        Assert.Equal("echo ok", okNote.Command);

        var badNote = Assert.Single(received, f => f.Id == bad.Id);
        Assert.Equal(BackgroundShellStatus.Completed, badNote.Status);
        Assert.Equal(7, badNote.ExitCode);
    }

    [Fact]
    public async Task Completion_notification_fires_for_killed_shell()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        BackgroundShellFinished? note = null;
        using var gate = new SemaphoreSlim(0);
        reg.OnShellFinished = f => { note = f; gate.Release(); };

        var shell = reg.Start("sleep 30", Path.GetTempPath());
        await Task.Delay(300);   // 프로세스가 확실히 뜰 시간
        await new KillShellTool(reg).ExecuteAsync(
            JsonDocument.Parse("""{"shell_id":"bash_1"}""").RootElement,
            new ToolContext(Path.GetTempPath(), PermissionMode.Auto), default).ToListAsync();

        await gate.WaitAsync(TimeSpan.FromSeconds(10));
        await shell.Completion;

        Assert.NotNull(note);
        Assert.Equal(shell.Id, note!.Id);
        Assert.Equal(BackgroundShellStatus.Killed, note.Status);
    }

    [Fact]
    public void Group_is_killed_before_the_cancel_callbacks_run()
    {
        // 창을 닫으면(SIGHUP) 종료 처리 중 CliWrap 취소 콜백이 늦어지는 사이 프로세스가 죽어, 그룹 SIGKILL 까지
        // 가지 못하고 백그라운드 셸이 고아로 남았다. 이제 그룹을 먼저 죽인다 — 콜백이 돌 때는 이미 없어야 한다.
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/setsid"))
        {
            return;
        }

        using var p = System.Diagnostics.Process.Start("/usr/bin/setsid", "sleep 30")!;
        var pid = p.Id;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (UnixSignal.kill(-pid, 0) != 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);   // setsid 가 그룹을 만들 때까지
        }

        Assert.Equal(0, UnixSignal.kill(-pid, 0));

        using var cts = new CancellationTokenSource();
        bool? aliveAtCancel = null;
        // 콜백 안에서 1초까지 기다려 본다. 그룹 킬이 먼저 나갔으면 곧 죽는다(좀비 'Z' 도 죽은 것 — 부모가 거두기 전까지
        // kill(-pid, 0) 은 성공하므로 그것으로는 판정하지 않는다). 콜백이 먼저면 킬이 아직 안 나가 1초 내내 살아 있다.
        cts.Token.Register(() =>
        {
            var until = DateTime.UtcNow.AddSeconds(1);
            while (IsAlive(pid) && DateTime.UtcNow < until)
            {
                Thread.Sleep(10);
            }

            aliveAtCancel = IsAlive(pid);
        });

        BashTool.KillProcessTree(pid, cts);

        Assert.False(aliveAtCancel);
        Assert.True(p.WaitForExit(5_000));
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)..][0] != 'Z';
        }
        catch (IOException)
        {
            return false;   // 이미 거둬짐
        }
    }
}
