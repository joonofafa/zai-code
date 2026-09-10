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

    private readonly TerminalMode _mode;
    private readonly TerminalInput _input;
    private readonly EventHandler _onProcessExit;
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
