using System.Text;
using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Bash;
using MoaiCode.Tui;
using MoaiCode.Tui.Input;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 백그라운드 셸 완료 → 자동 재개 턴 경로: 통보 큐의 제외 규칙(직접 kill·종료 후 읽음)과 연속 상한,
/// 입력 대기 깨움 시 초안 보존, 재개 턴의 composer 턴 모드 전환. 도크 테스트가 Console.Out 과
/// 전역(PromptInterrupt·TerminalInput.Shared)을 바꾸므로 다른 콘솔 스왑 테스트와 직렬화한다.
/// </summary>
[Collection("EnvMutating")]
public class BackgroundResumeTests
{
    private static BackgroundShellFinished Fin(string id) =>
        new(id, "cmd", BackgroundShellStatus.Completed, 0);

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

    [Fact]
    public void Drain_skips_shells_the_model_already_knows()
    {
        var q = new BackgroundResumeQueue(id => id == "bash_2");
        q.Enqueue(Fin("bash_1"));
        q.Enqueue(Fin("bash_2"));
        q.Enqueue(Fin("bash_3"));

        var drained = q.Drain();

        Assert.Equal(["bash_1", "bash_3"], drained.Select(f => f.Id));
        Assert.Empty(q.Drain());   // 제외된 것도 큐에서는 빠진다(다시 깨우지 않음)
    }

    [Fact]
    public void HasPending_ignores_acknowledged_shells()
    {
        var q = new BackgroundResumeQueue(id => id == "bash_1");
        Assert.False(q.HasPending);

        q.Enqueue(Fin("bash_1"));
        Assert.False(q.HasPending);   // 이미 아는 셸뿐이면 입력 대기를 깨우지 않는다

        q.Enqueue(Fin("bash_2"));
        Assert.True(q.HasPending);
    }

    [Fact]
    public void Resume_streak_caps_and_resets_on_user_input()
    {
        var q = new BackgroundResumeQueue(_ => false);
        for (var i = 0; i < BackgroundResumeQueue.MaxConsecutive; i++)
        {
            Assert.True(q.TryBeginResume());
        }

        Assert.False(q.TryBeginResume());   // 입력 없이 상한만큼 이어졌으면 멈춘다
        Assert.False(q.TryBeginResume());

        q.OnUserInput();
        Assert.True(q.TryBeginResume());
    }

    [Fact]
    public async Task Shell_killed_by_the_model_is_acknowledged()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var shell = reg.Start("sleep 30", Path.GetTempPath());
        Assert.False(shell.Acknowledged);

        await Run(new KillShellTool(reg), """{"shell_id":"bash_1"}""");
        await shell.Completion;

        Assert.Equal(BackgroundShellStatus.Killed, shell.Status);
        Assert.True(shell.Acknowledged);
    }

    [Fact]
    public async Task Reading_after_exit_acknowledges_but_reading_while_running_does_not()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var running = reg.Start("echo a; sleep 30", Path.GetTempPath());
        var done = reg.Start("echo b", Path.GetTempPath());
        await done.Completion;

        await Run(new BashOutputTool(reg), """{"shell_id":"bash_1"}""");
        Assert.False(running.Acknowledged);   // 아직 도는 셸은 끝나면 보고해야 한다

        await Run(new BashOutputTool(reg), """{"shell_id":"bash_2"}""");
        Assert.True(done.Acknowledged);       // 종료 후 끝까지 읽음 — 보고 불필요

        running.Kill();
        await running.Completion;
    }

    [Fact]
    public async Task KillShell_on_a_finished_shell_acknowledges_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var shell = reg.Start("echo done", Path.GetTempPath());
        await shell.Completion;

        var text = await Run(new KillShellTool(reg), """{"shell_id":"bash_1"}""");
        Assert.Contains("exit code 0", text);   // 최종 상태를 모델에 알려줬다
        Assert.True(shell.Acknowledged);        // → 완료 보고를 한 번 더 띄우지 않는다
    }

    [Fact]
    public async Task ReadNew_returns_the_status_seen_at_read_time()
    {
        if (OperatingSystem.IsWindows()) return;

        var reg = new BackgroundShellRegistry();
        var shell = reg.Start("echo out; exit 4", Path.GetTempPath());
        await shell.Completion;

        var (text, _, status, exitCode) = shell.ReadNew();
        Assert.Contains("out", text);
        Assert.Equal(BackgroundShellStatus.Completed, status);
        Assert.Equal(4, exitCode);
    }

    // 셸이 띄우고 떨어져 나간 자식(init 에 재부모)도 KillShell 의 그룹 종료에 함께 죽는다.
    [Fact]
    public async Task KillShell_also_kills_detached_children()
    {
        if (OperatingSystem.IsWindows() || !File.Exists("/usr/bin/setsid")) return;

        var reg = new BackgroundShellRegistry();
        var shell = reg.Start("(sleep 300 >/dev/null 2>&1 & echo $!); sleep 300", Path.GetTempPath());
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (shell.UnreadLength == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var orphanPid = int.Parse(shell.ReadNew().Text.Trim());
        Assert.True(Directory.Exists($"/proc/{orphanPid}"));

        await Run(new KillShellTool(reg), """{"shell_id":"bash_1"}""");
        await shell.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (IsAlive(orphanPid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(IsAlive(orphanPid));
    }

    // /proc/<pid>/stat 의 상태가 좀비(Z)면 이미 죽은 것(부모 수거 대기).
    private static bool IsAlive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException)
        {
            return false;
        }
    }

    // 끝나지 않는 stdin — 키 없이 대기 루프만 돌게 한다(리더 스레드는 백그라운드라 프로세스와 함께 정리).
    private sealed class SilentStream : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Wake_keeps_draft_and_resume_turn_parks_composer_without_clearing_it()
    {
        var origOut = Console.Out;
        var origShared = TerminalInput.Shared;
        var origWake = PromptInterrupt.ShouldWake;
        var captured = new StringBuilder();
        using var input = new TerminalInput(new SilentStream());
        try
        {
            Console.SetOut(new StringWriter(captured));
            TerminalInput.Shared = input;
            PromptInterrupt.ShouldWake = () => true;

            var dock = new BottomDock(() => "status");
            var history = Array.Empty<string>();
            var slash = Array.Empty<string>();

            // 입력 대기 중 깨움: 센티넬 반환, 쓰던 초안은 그대로.
            Assert.Equal(PromptInterrupt.WakeSignal, dock.ReadLine(history, slash, null, initialText: "half typed"));
            Assert.Equal("half typed", dock.CurrentText);
            Assert.False(dock.InTurn);

            // 재개 턴: echo 없이 턴 모드로, 초안 보존.
            captured.Clear();
            dock.ParkForTurn();
            Assert.True(dock.InTurn);
            Assert.Equal("half typed", dock.CurrentText);
            Assert.Contains("\u001b7", captured.ToString());   // 턴 모드 그리기 = 출력 커서 저장/복원
            dock.EndTurnMode();

            // 턴이 끝나고 다시 대기에 들어가도(시드 없음) 초안이 이어진다.
            Assert.Equal(PromptInterrupt.WakeSignal, dock.ReadLine(history, slash, null));
            Assert.Equal("half typed", dock.CurrentText);

            // 큐의 슬래시·셸 명령이 composer 를 해체해도 다음 프롬프트에 초안이 남는다.
            dock.Teardown();
            Assert.Equal(PromptInterrupt.WakeSignal, dock.ReadLine(history, slash, null));
            Assert.Equal("half typed", dock.CurrentText);

            // 큐의 일반 메시지를 턴으로 echo 해도 입력 중 초안은 그대로.
            dock.KeepComposerForTurn("queued message", keepDraft: true);
            Assert.True(dock.InTurn);
            Assert.Equal("half typed", dock.CurrentText);
        }
        finally
        {
            Console.SetOut(origOut);
            TerminalInput.Shared = origShared;
            PromptInterrupt.ShouldWake = origWake;
        }
    }

    private static BottomDock DockOver(Stream stdin, out TerminalInput input)
    {
        input = new TerminalInput(stdin);
        TerminalInput.Shared = input;
        return new BottomDock(() => "status");
    }

    // 제출하며 해체한 입력은 버퍼에서 비운다 — 재개 턴이 composer 를 다시 세워도 이미 실행한 명령이 안 살아난다.
    [Fact]
    public void Submitted_slash_command_does_not_come_back_when_resume_turn_reinstalls()
    {
        var origOut = Console.Out;
        var origShared = TerminalInput.Shared;
        try
        {
            Console.SetOut(new StringWriter());
            var dock = DockOver(new MemoryStream(Encoding.UTF8.GetBytes("/help\r")), out var input);
            using (input)
            {
                Assert.Equal("/help", dock.ReadLine([], [], null));
                Assert.Equal(string.Empty, dock.CurrentText);

                dock.ParkForTurn();
                Assert.Equal(string.Empty, dock.CurrentText);
            }
        }
        finally
        {
            Console.SetOut(origOut);
            TerminalInput.Shared = origShared;
        }
    }

    // 큐의 슬래시 명령이 composer 를 해체한 뒤 일반 메시지가 오면, 먼저 다시 세운 다음 턴 모드로 간다.
    [Fact]
    public void KeepComposerForTurn_reinstalls_a_torn_down_composer()
    {
        var origOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            var dock = new BottomDock(() => "status");
            dock.KeepComposerForTurn("queued message");

            Assert.True(dock.InTurn);
            Assert.True((int)typeof(BottomDock).GetField("_reserved",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(dock)! > 0);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    // 줄 입력 백스페이스: 서로게이트 쌍(이모지)은 한 번에 통째로 지운다(반쪽 문자 남지 않음).
    [Fact]
    public void Line_prompt_backspace_removes_whole_surrogate_pair()
    {
        var origOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            using var input = new TerminalInput(new MemoryStream(Encoding.UTF8.GetBytes("a\U0001F600\u007f\r")));
            Assert.Equal("a", input.ReadLine());
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    // 입력창 편집: Backspace·Delete·←/→ 가 서로게이트 쌍(이모지)을 반쪽만 다뤄 깨진 글자(�)가 제출됐다.
    [Theory]
    [InlineData("x\U0001F600\u007f\r", "x")]                          // Backspace
    [InlineData("x\U0001F600y\u001b[D\u001b[Dz\r", "xz\U0001F600y")]  // ← 두 번 = 글자 두 개
    [InlineData("x\U0001F600\u001b[H\u001b[C\u001b[3~\r", "x")]       // Home, →, Delete
    public void Composer_edits_whole_surrogate_pairs(string keys, string expected)
    {
        var origOut = Console.Out;
        var origShared = TerminalInput.Shared;
        try
        {
            Console.SetOut(new StringWriter());
            var dock = DockOver(new MemoryStream(Encoding.UTF8.GetBytes(keys)), out var input);
            using (input)
            {
                Assert.Equal(expected, dock.ReadLine([], [], null));
            }
        }
        finally
        {
            Console.SetOut(origOut);
            TerminalInput.Shared = origShared;
        }
    }

    // 턴 중 ↑/↓: 프롬프트를 열 때의 히스토리 위치가 방금 보낸 줄이 추가된 뒤에도 그대로라, ↑ 가 방금 보낸 줄을
    // 건너뛰고 ↓ 로 돌아와도 쓰던 초안이 사라졌다.
    [Fact]
    public void History_during_a_turn_starts_at_the_just_sent_line_and_keeps_the_draft()
    {
        var origOut = Console.Out;
        var origShared = TerminalInput.Shared;
        try
        {
            Console.SetOut(new StringWriter());
            var history = new List<string> { "older" };
            var dock = DockOver(new MemoryStream(Encoding.UTF8.GetBytes("just-sent\r")), out var input);
            using (input)
            {
                Assert.Equal("just-sent", dock.ReadLine(history, [], null));
                history.Add("just-sent");   // ReplApp.AddHistory
                dock.KeepComposerForTurn("just-sent");

                foreach (var c in "draft")
                {
                    dock.HandleEvent(new KeyEvent(new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false)));
                }

                dock.HandleEvent(new KeyEvent(new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)));
                Assert.Equal("just-sent", dock.CurrentText);
                dock.HandleEvent(new KeyEvent(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false)));
                Assert.Equal("draft", dock.CurrentText);
            }
        }
        finally
        {
            Console.SetOut(origOut);
            TerminalInput.Shared = origShared;
        }
    }
}
