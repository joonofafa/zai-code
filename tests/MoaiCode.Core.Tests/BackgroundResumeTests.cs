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

            // 대비: 슬래시 명령 턴용 재설치는 초안을 비운다.
            dock.ReinstallForTurn();
            Assert.True(dock.InTurn);
            Assert.Equal(string.Empty, dock.CurrentText);
        }
        finally
        {
            Console.SetOut(origOut);
            TerminalInput.Shared = origShared;
            PromptInterrupt.ShouldWake = origWake;
        }
    }
}
