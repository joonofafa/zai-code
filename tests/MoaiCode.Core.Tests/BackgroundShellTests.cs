using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Bash;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>Bash(run_in_background) → BashOutput → KillShell 왕복. Unix 셸 전제(Windows 는 실기 검증).</summary>
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

        var (text, truncated) = shell.ReadNew();
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
}
