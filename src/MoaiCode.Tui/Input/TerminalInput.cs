using System.Text;

namespace MoaiCode.Tui.Input;

/// <summary>
/// stdin 원시 바이트를 백그라운드에서 읽어 <see cref="VtParser"/> 로 이벤트화하고, 소비처가 쓰던
/// <see cref="ConsoleKeyInfo"/> 형태로도 돌려주는 입력 리더. 기존 <c>Console.ReadKey</c>/
/// <c>BracketedPaste.ReadKey</c> 를 대체한다.
///
/// <para>단독 ESC 는 뒤에 시퀀스가 이어질 수 있어, 큐가 비고 파서에 ESC 만 대기 중이면 짧게(<see cref="EscTimeoutMs"/>)
/// 기다렸다 더 안 오면 Escape 로 확정한다(ESC 타임아웃).</para>
///
/// 테스트를 위해 입력 <see cref="Stream"/> 을 주입할 수 있다(기본은 표준 입력).
/// </summary>
public sealed class TerminalInput : IDisposable
{
    private const int EscTimeoutMs = 50;

    /// <summary>
    /// REPL 세션이 활성인 동안(raw 모드)만 세팅되는 공용 리더. 키 전용 소비처(피커·비밀번호·ESC 워처)가
    /// <c>Shared?.ReadKey() ?? Console.ReadKey(...)</c> 로 참조한다 — null 이면(로그인/셋업 등 cooked 구간)
    /// 기존 Console 경로로 폴백. char 편집은 raw, Console.ReadLine 구간은 cooked 로 공존시키기 위함.
    /// </summary>
    public static TerminalInput? Shared { get; internal set; }

    private readonly Stream _stdin;
    private readonly VtParser _parser = new();
    private readonly object _lock = new();
    private readonly Queue<InputEvent> _events = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _reader;
    private volatile bool _eof;
    private volatile bool _stopped;

    public TerminalInput(Stream? stdin = null)
    {
        _stdin = stdin ?? OpenRawStdin();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "moai-input" };
        _reader.Start();
    }

    /// <summary>
    /// 터미널 입력을 '가공 없이' 읽는 스트림. 유닉스에서 <see cref="Console.OpenStandardInput"/> 은
    /// stdin 이 터미널이면 .NET 내부 줄편집기(StdInReader)를 태우는 스트림을 돌려준다 — 문자를 스스로
    /// 에코하고 Enter 까지 모아 두므로, raw 모드로 한 키씩 처리하는 이 리더에는 바이트가 제때 오지 않는다
    /// (Tab 자동완성·Shift+Tab 모드전환이 먹지 않고, 한글 백스페이스가 어긋나고, 에코가 입력창 밖에
    /// 찍히던 원인). 그래서 fd 0 을 직접 연다. 윈도우는 그런 가공이 없어 기존 경로를 그대로 쓴다.
    /// </summary>
    private static Stream OpenRawStdin()
    {
        if (OperatingSystem.IsWindows())
        {
            return Console.OpenStandardInput();
        }

        try
        {
            // ownsHandle:false — fd 0 은 프로세스 공용이라 이 스트림이 닫아서는 안 된다.
            // bufferSize:1 — FileStream 자체 버퍼링을 끄고 read(2) 결과를 즉시 넘긴다.
            return new FileStream(
                new Microsoft.Win32.SafeHandles.SafeFileHandle((IntPtr)0, ownsHandle: false),
                FileAccess.Read, bufferSize: 1, isAsync: false);
        }
        catch
        {
            return Console.OpenStandardInput();   // 열 수 없으면 기존 경로로 폴백
        }
    }

    private void ReadLoop()
    {
        var buf = new byte[1024];
        while (!_stopped)
        {
            int n;
            try
            {
                n = _stdin.Read(buf, 0, buf.Length);
            }
            catch
            {
                break;
            }

            if (n <= 0)
            {
                _eof = true;
                _signal.Set();
                return;
            }

            lock (_lock)
            {
                foreach (var ev in _parser.Push(buf.AsSpan(0, n)))
                {
                    _events.Enqueue(ev);
                }
            }

            _signal.Set();
        }
    }

    /// <summary>다음 입력 이벤트를 블로킹으로 반환. stdin 종료(EOF) 시 null.</summary>
    public InputEvent? ReadEvent()
    {
        while (true)
        {
            lock (_lock)
            {
                if (_events.Count > 0)
                {
                    return _events.Dequeue();
                }

                if (_eof)
                {
                    return null;
                }
            }

            bool pendingEsc;
            lock (_lock)
            {
                pendingEsc = _parser.PendingIsEscape;
            }

            if (pendingEsc)
            {
                _signal.WaitOne(EscTimeoutMs);
                lock (_lock)
                {
                    if (_events.Count == 0 && _parser.PendingIsEscape)
                    {
                        foreach (var ev in _parser.Flush())
                        {
                            _events.Enqueue(ev);
                        }
                    }
                }
            }
            else
            {
                _signal.WaitOne();
            }
        }
    }

    /// <summary>
    /// 최대 <paramref name="timeoutMs"/> ms 기다려 이벤트를 반환(없으면 null). 리사이즈 폴링 루프처럼
    /// 블로킹하지 않고 간헐 확인이 필요한 곳에서 쓴다. 대기 중 단독 ESC 는 ESC 타임아웃으로 확정.
    /// </summary>
    public InputEvent? TryReadEvent(int timeoutMs)
    {
        lock (_lock)
        {
            if (_events.Count > 0)
            {
                return _events.Dequeue();
            }

            if (_eof)
            {
                return null;
            }
        }

        bool pendingEsc;
        lock (_lock)
        {
            pendingEsc = _parser.PendingIsEscape;
        }

        var wait = pendingEsc ? Math.Min(timeoutMs, EscTimeoutMs) : timeoutMs;
        _signal.WaitOne(wait);

        lock (_lock)
        {
            if (_events.Count == 0 && _parser.PendingIsEscape)
            {
                foreach (var ev in _parser.Flush())
                {
                    _events.Enqueue(ev);
                }
            }

            return _events.Count > 0 ? _events.Dequeue() : null;
        }
    }

    /// <summary>
    /// 다음 키를 <see cref="ConsoleKeyInfo"/> 로 반환(키 전용 소비처: 피커·비밀번호 등). Paste/Mouse/Focus 는
    /// 건너뛰고, Ctrl+C(Cancel)는 Escape 로 매핑해 취소로 이어지게 한다. EOF 시 default.
    /// </summary>
    public ConsoleKeyInfo ReadKey()
    {
        while (true)
        {
            var ev = ReadEvent();
            switch (ev)
            {
                case null:
                    return default;
                case KeyEvent k:
                    return k.Key;
                case CancelEvent:
                    return new ConsoleKeyInfo('\u001b', ConsoleKey.Escape, shift: false, alt: false, control: false);
                default:
                    continue; // Paste/Mouse/Focus 는 키 전용 소비처에서 무시
            }
        }
    }

    /// <summary>
    /// 한 줄 입력을 읽는다(raw 모드에서 Console.ReadLine 대체). 인쇄 문자는 에코(또는 <paramref name="mask"/>
    /// 면 '*'), Backspace 는 지우고, Enter 로 확정. Ctrl+C/EOF 는 null. 로그인·설정 등 단순 라인 프롬프트용.
    /// </summary>
    public string? ReadLine(bool mask = false)
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var ev = ReadEvent();
            switch (ev)
            {
                case null:
                case CancelEvent:
                    return null;
                case PasteEvent p:
                    foreach (var ch in p.Text.Replace("\r\n", "\n").Replace('\r', '\n'))
                    {
                        if (ch == '\n')
                        {
                            continue; // 라인 프롬프트에 붙여넣은 개행은 무시(줄 확정은 Enter 로만)
                        }

                        sb.Append(ch);
                        Console.Write(mask ? '*' : ch);
                    }

                    break;
                case KeyEvent k:
                    var key = k.Key;
                    if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
                    {
                        Console.Write('\n');
                        return sb.ToString();
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Remove(sb.Length - 1, 1);
                            Console.Write("\b \b");
                        }

                        break;
                    }

                    if (!char.IsControl(key.KeyChar))
                    {
                        sb.Append(key.KeyChar);
                        Console.Write(mask ? '*' : key.KeyChar);
                    }

                    break;
                // Mouse/Focus 무시
            }
        }
    }

    /// <summary>대기 중인 이벤트가 있는가(비블로킹). 리사이즈 폴링 루프 등에서 사용.</summary>
    public bool Available
    {
        get { lock (_lock) { return _events.Count > 0; } }
    }

    public void Dispose()
    {
        _stopped = true;
        _signal.Set();
        // 리더는 백그라운드 스레드라 프로세스 종료와 함께 정리된다. stdin.Read 블로킹을 강제로
        // 깨우지는 않는다(콘솔 stdin 은 close 해도 즉시 안 풀릴 수 있어 join 하지 않는다).
    }
}
