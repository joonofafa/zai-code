using System.Reflection;
using System.Text;
using MoaiCode.Tui;
using MoaiCode.Tui.Input;
using Xunit;
using System.Threading.Tasks;

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

    // CPR: DSR(ESC[6n) 질의에 대한 터미널 응답 ESC[<row>;<col>R 을 별도 이벤트로 파싱(키로 오인 금지).
    [Fact]
    public void Parser_turns_CPR_into_CursorReportEvent()
    {
        var evs = Drain([0x1b, (byte)'[', (byte)'2', (byte)'8', (byte)';', (byte)'1', (byte)'R'], 1);
        var cpr = Assert.IsType<CursorReportEvent>(Assert.Single(evs));
        Assert.Equal(28, cpr.Row);
        Assert.Equal(1, cpr.Col);
    }

    [Fact]
    public void Parser_leaves_CPR_before_key_events_intact()
    {
        var bytes = new List<byte> { 0x1b, (byte)'[', (byte)'5', (byte)';', (byte)'7', (byte)'R' }
            .Concat(Encoding.UTF8.GetBytes("hi")).ToArray();
        var evs = Drain(bytes, 3);
        Assert.IsType<CursorReportEvent>(evs[0]);
        Assert.Equal('h', Assert.IsType<KeyEvent>(evs[1]).Key.KeyChar);
        Assert.Equal('i', Assert.IsType<KeyEvent>(evs[2]).Key.KeyChar);
    }

    // CPR 미형식(파라미터 1개)은 키로도 오인하지 않고 조용히 무시된다 — 다른 CSI 와 동일하게.
    [Fact]
    public void Parser_ignores_malformed_CPR()
    {
        var evs = Drain([0x1b, (byte)'[', (byte)'R'], 1);
        Assert.Empty(evs);
    }

    // DSR 질의 → 응답 대기 왕복: 쿼리 후 도착한 응답을 읽어 (row,col) 로 반환하고, 응답 뒤에 온
    // 사용자 키는 큐로 되돌려 순서를 보존한다(리사이즈 처리 중 타이핑 유실 방지).
    // 응답은 쿼리 시작 "후"에 도착해야 한다 — QueryCursor 는 시작 시 큐에 남은 CPR(타임아웃으로
    // 포기한 낡은 응답)을 선배수해 버리므로, 미리 넣어두면 스테일로 간주돼 버려진다.
    private sealed class TimedStream : Stream
    {
        private readonly object _sync = new();
        private readonly Queue<byte> _buf = new();
        private bool _eof;

        public void Push(byte[] data)
        {
            lock (_sync)
            {
                foreach (var b in data)
                {
                    _buf.Enqueue(b);
                }
                Monitor.PulseAll(_sync);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_sync)
            {
                while (_buf.Count == 0 && !_eof)
                {
                    Monitor.Wait(_sync);
                }
                if (_buf.Count == 0)
                {
                    return 0;
                }
                var n = Math.Min(count, _buf.Count);
                for (var i = 0; i < n; i++)
                {
                    buffer[offset + i] = _buf.Dequeue();
                }
                return n;
            }
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
    public async Task QueryCursor_returns_report_and_defers_later_events()
    {
        var resp = new byte[] { 0x1b, (byte)'[', (byte)'2', (byte)'8', (byte)';', (byte)'1', (byte)'R' }
            .Concat(Encoding.UTF8.GetBytes("xy")).ToArray();
        var stream = new TimedStream();
        using var input = new TerminalInput(stream);

        // QueryCursor 는 시작 시 큐의 낡은 CPR 을 선배수(drain)하므로 응답은 drain 이후에 도착해야
        // 한다. drain 은 메서드 첫머리(락 + 빈 큐 LINQ)라 호출 즉시(µs) 끝나고, 타임아웃은 2000ms —
        // 30ms 뒤 푸시는 양쪽 마진이 수십 배여야 흔들리지 않는다(편차가 나도 항상 창 안).
        var push = Task.Run(async () =>
        {
            await Task.Delay(30);
            stream.Push(resp);
        });
        var got = input.QueryCursor(2000);
        Assert.Equal((28, 1), got);
        Assert.Equal('x', Assert.IsType<KeyEvent>(input.ReadEvent()).Key.KeyChar);
        Assert.Equal('y', Assert.IsType<KeyEvent>(input.ReadEvent()).Key.KeyChar);
        await push;
    }

    // 타임아웃으로 포기한 뒤 늦게 도착한 CPR 은 다음 쿼리가 읽지 못하게 선배수돼야 한다
    // (빠른 연속 리사이즈에서 낡은 응답이 shift 계산을 오염시키는 것 방지).
    [Fact]
    public async Task QueryCursor_discards_stale_report_from_previous_timeout()
    {
        var stale = new byte[] { 0x1b, (byte)'[', (byte)'9', (byte)';', (byte)'1', (byte)'R' };
        var fresh = new byte[] { 0x1b, (byte)'[', (byte)'5', (byte)';', (byte)'7', (byte)'R' };
        var stream = new TimedStream();
        using var input = new TerminalInput(stream);

        // 1차: 응답 없이 타임아웃 → 무응답(null).
        Assert.Null(input.QueryCursor(80));

        // 1차의 낡은 응답이 도착해 큐에 적재됐음를 상태로 확인(고정 sleep 아님).
        stream.Push(stale);
        for (var i = 0; i < 400 && !input.Available; i++)
        {
            await Task.Delay(5);
        }
        Assert.True(input.Available, "stale report should be queued before the second query");

        // 2차: 시작 시 낡은 (9,1) 이 선배수되고, 창 안에 도착한 새 응답 (5,7) 이 정확히 반환된다.
        // (1차와 같은 이유로 drain(µs) 이후·타임아웃(2000ms) 이전의 30ms 고정 지연은 마진 충분.)
        var push = Task.Run(async () =>
        {
            await Task.Delay(30);
            stream.Push(fresh);
        });
        var got = input.QueryCursor(2000);
        Assert.Equal((5, 7), got);
        await push;
    }
}
