using System.Reflection;
using System.Text;
using MoaiCode.Tui;
using MoaiCode.Tui.Input;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// Tab 자동완성·Shift+Tab 모드전환이 실환경에서 먹지 않는 문제의 유실 지점을 좁히는 테스트.
/// pty 하네스는 입력 전송 방식에 따라 결과가 갈려 신뢰할 수 없으므로, stdin 스트림을 주입해
/// 리더→파서→BottomDock 을 그대로 태운다(터미널 없이 앱 내부만 검증).
/// </summary>
public class TabInputPipelineTests
{
    private static readonly string[] Slash = ["clear", "model", "resume"];

    // reader(TerminalInput) → VtParser 까지: 바이트가 이벤트로 나오는가.
    private static List<InputEvent> Drain(byte[] bytes, int expected)
    {
        using var input = new TerminalInput(new MemoryStream(bytes));
        var got = new List<InputEvent>();
        for (var i = 0; i < expected; i++)
        {
            var ev = input.ReadEvent();
            if (ev is null) break;
            got.Add(ev);
        }
        return got;
    }

    [Fact]
    public void Reader_turns_0x09_into_Tab()
    {
        var evs = Drain([0x09], 1);
        var key = Assert.IsType<KeyEvent>(Assert.Single(evs)).Key;
        Assert.Equal(ConsoleKey.Tab, key.Key);
        Assert.False(key.Modifiers.HasFlag(ConsoleModifiers.Shift));
    }

    [Fact]
    public void Reader_turns_ESC_bracket_Z_into_shift_Tab()
    {
        var evs = Drain([0x1b, (byte)'[', (byte)'Z'], 1);
        var key = Assert.IsType<KeyEvent>(Assert.Single(evs)).Key;
        Assert.Equal(ConsoleKey.Tab, key.Key);
        Assert.True(key.Modifiers.HasFlag(ConsoleModifiers.Shift));
    }

    // 실제 타이핑 순서 그대로: "/mo" 를 친 뒤 Tab, 이어서 Shift+Tab.
    [Fact]
    public void Reader_keeps_Tab_after_typed_text()
    {
        var bytes = new List<byte>(Encoding.UTF8.GetBytes("/mo")) { 0x09, 0x1b, (byte)'[', (byte)'Z' };
        var evs = Drain([.. bytes], 5);
        Assert.Equal(5, evs.Count);
        var tab = Assert.IsType<KeyEvent>(evs[3]).Key;
        var shiftTab = Assert.IsType<KeyEvent>(evs[4]).Key;
        Assert.Equal(ConsoleKey.Tab, tab.Key);
        Assert.False(tab.Modifiers.HasFlag(ConsoleModifiers.Shift));
        Assert.Equal(ConsoleKey.Tab, shiftTab.Key);
        Assert.True(shiftTab.Modifiers.HasFlag(ConsoleModifiers.Shift));
    }

    // 회귀 방지: 유닉스에서 Console.OpenStandardInput() 은 stdin 이 터미널일 때 .NET 내부 줄편집기를
    // 태우는 스트림을 준다(문자를 스스로 에코하고 Enter 까지 버퍼링). 그래서 fd 0 을 직접 연다.
    // 이걸 되돌리면 Tab·Shift+Tab 이 먹지 않고 한글 백스페이스가 어긋난다.
    [Fact]
    public void Reader_opens_fd0_directly_on_unix()
    {
        if (OperatingSystem.IsWindows()) return;

        var open = typeof(TerminalInput).GetMethod("OpenRawStdin",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(open);

        using var stream = (Stream)open!.Invoke(null, null)!;
        var fs = Assert.IsType<FileStream>(stream);
        Assert.Equal(0, (int)fs.SafeFileHandle.DangerousGetHandle());
    }

    private static BottomDock Dock(string text, out StringBuilder buf)
    {
        var dock = new BottomDock(() => "status");
        var t = typeof(BottomDock);
        buf = (StringBuilder)t.GetField("_buf", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dock)!;
        buf.Append(text);
        t.GetField("_pos", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dock, text.Length);
        t.GetField("_slash", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dock, Slash);
        return dock;
    }

    // 도크까지 온 Tab 이 실제로 자동완성을 수행하는가(ReadLine 이 넣어주는 상태를 그대로 재현).
    [Fact]
    public void Dock_completes_slash_command_on_Tab()
    {
        var dock = Dock("/mo", out var buf);
        dock.HandleEvent(new KeyEvent(new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false)));
        Assert.Equal("/model ", buf.ToString());
    }

    [Fact]
    public void Dock_cycles_mode_on_shift_Tab()
    {
        var dock = Dock("", out _);
        var cycled = 0;
        typeof(BottomDock).GetField("_cycleMode", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(dock, new Func<string>(() => { cycled++; return "status"; }));

        dock.HandleEvent(new KeyEvent(new ConsoleKeyInfo('\t', ConsoleKey.Tab, true, false, false)));
        Assert.Equal(1, cycled);
    }
}
