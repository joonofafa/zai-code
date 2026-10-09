namespace MoaiCode.Tui.Input;

/// <summary>
/// 대화형 REPL 입력 세션. 콘솔을 raw/VT 모드로 두고(<see cref="TerminalMode"/>) 원시 바이트 리더
/// (<see cref="TerminalInput"/>)를 띄운 뒤 bracketed paste·포커스·마우스 VT 기능을 켠다. dispose 하면
/// 그 기능들을 끄고 리더를 정리하고 콘솔 모드를 복원한다.
///
/// <para>raw 모드는 REPL 루프 범위로만 유지한다 — 로그인/셋업의 <c>Console.ReadLine</c> 이 cooked 모드를
/// 필요로 하므로 전역으로 켜지 않는다. 세션이 활성인 동안 <see cref="TerminalInput.Shared"/> 를 세팅해
/// 키 전용 소비처가 같은 리더를 쓰게 한다.</para>
///
/// <para>크래시/비정상 종료로 raw 모드가 복원 안 되면 셸이 먹통이 되므로, 복원을 <c>Dispose</c> 와
/// <see cref="AppDomain.ProcessExit"/> 양쪽에 건다.</para>
/// </summary>
public sealed class TerminalSession : IDisposable
{
    // bracketed paste + 포커스 리포팅. 종료 시 역순 해제.
    // 마우스 트래킹(?1000;1006)은 켜지 않는다 — 소비하는 UI 가 없는데 켜면 터미널이 휠/클릭을
    // 앱으로 삼켜 네이티브 스크롤백(마우스휠 위로)이 죽는다(Windows Terminal ssh 에서 실제 발생).
    private const string EnableFeatures = "\u001b[?2004h\u001b[?1004h";
    private const string DisableFeatures = "\u001b[?1004l\u001b[?2004l";

    // 시그널 종료 때 화면 상태도 되돌린다: 커서 저장 → 스크롤 영역 해제(DECSTBM 은 커서를 맨 위로 옮긴다) →
    // 커서 복원 → 커서 표시. 정상 종료는 입력창이 스스로 정리하므로 시그널 경로에서만 쓴다.
    private const string ResetScreen = "\u001b7\u001b[r\u001b8\u001b[?25h";

    private readonly TerminalMode _mode;
    private readonly TerminalInput _input;
    private readonly EventHandler _onProcessExit;
    private readonly List<System.Runtime.InteropServices.PosixSignalRegistration> _signals = new();
    private bool _disposed;

    public TerminalSession()
    {
        _mode = TerminalMode.Enter();
        _input = new TerminalInput();
        TerminalInput.Shared = _input;

        if (!Console.IsOutputRedirected)
        {
            Console.Write(EnableFeatures);
        }

        _onProcessExit = (_, _) => RestoreTerminal();
        AppDomain.CurrentDomain.ProcessExit += _onProcessExit;

        // SIGTERM(kill)·SIGHUP·SIGQUIT 에는 ProcessExit 가 오지 않아, 셸이 raw 모드(-icanon -echo)·남은 스크롤 영역·
        // 숨은 커서로 먹통이 됐다. 복원만 하고 기본 동작(종료)은 그대로 둔다(Cancel 안 함).
        foreach (var sig in new[]
                 {
                     System.Runtime.InteropServices.PosixSignal.SIGTERM,
                     System.Runtime.InteropServices.PosixSignal.SIGHUP,
                     System.Runtime.InteropServices.PosixSignal.SIGQUIT,
                 })
        {
            try
            {
                _signals.Add(System.Runtime.InteropServices.PosixSignalRegistration.Create(sig, _ =>
                {
                    try
                    {
                        if (!Console.IsOutputRedirected)
                        {
                            Console.Write(ResetScreen);
                        }
                    }
                    catch
                    {
                        // 터미널이 이미 닫혔을 수 있다(SIGHUP).
                    }

                    RestoreTerminal();
                }));
            }
            catch (PlatformNotSupportedException)
            {
                // 이 플랫폼에서 지원하지 않는 시그널
            }
        }
    }

    public TerminalInput Input => _input;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= _onProcessExit;
        foreach (var s in _signals)
        {
            s.Dispose();
        }

        _signals.Clear();
        RestoreTerminal();
        _input.Dispose();
    }

    private void RestoreTerminal()
    {
        try
        {
            if (!Console.IsOutputRedirected)
            {
                Console.Write(DisableFeatures);
            }
        }
        catch
        {
            // 무시 — 종료 경로.
        }

        if (ReferenceEquals(TerminalInput.Shared, _input))
        {
            TerminalInput.Shared = null;
        }

        _mode.Dispose();   // 콘솔 모드 복원(에코/캐노니컬)
    }
}
