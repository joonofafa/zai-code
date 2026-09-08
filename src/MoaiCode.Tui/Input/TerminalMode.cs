using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MoaiCode.Tui.Input;

/// <summary>
/// 콘솔을 raw/VT 입력 모드로 두고, 원래 모드를 저장했다가 복원한다(<see cref="IDisposable"/>).
/// 이 모드라야 stdin 이 <c>ESC[200~</c>(bracketed paste)·화살표 등 VT 시퀀스를 원시 바이트로
/// 흘려보내고, <see cref="VtParser"/> 가 해석할 수 있다.
///
/// <para>Windows: <c>SetConsoleMode</c> P/Invoke — 입력에 ENABLE_VIRTUAL_TERMINAL_INPUT 켜고
/// LINE/ECHO/PROCESSED 끔(Ctrl+C 는 파서가 0x03→Cancel 로 처리), 출력에 VT_PROCESSING 보장.</para>
/// <para>Unix: <c>stty</c> — 현재 설정을 <c>-g</c> 로 저장하고 raw(-echo -icanon -isig, min 1 time 0)로
/// 전환, 종료 시 저장값 복원. termios 구조체 오프셋 포킹보다 이식성·견고성이 낫다.</para>
///
/// 어느 쪽이든 실패하면 조용히 무시하고(리다이렉트/콘솔 없음 등) degrade — 크래시하지 않는다.
/// </summary>
public sealed class TerminalMode : IDisposable
{
    private readonly Action? _restore;
    private bool _disposed;

    private TerminalMode(Action? restore) => _restore = restore;

    /// <summary>raw/VT 모드로 진입. 반환된 객체를 dispose 하면 원래 모드로 복원된다.</summary>
    public static TerminalMode Enter()
    {
        try
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return new TerminalMode(null); // 대화형 콘솔이 아님 — 아무것도 하지 않음
            }

            return OperatingSystem.IsWindows() ? EnterWindows() : EnterUnix();
        }
        catch
        {
            return new TerminalMode(null); // best-effort
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _restore?.Invoke();
        }
        catch
        {
            // 복원 실패는 무시 — 종료 경로라 더 할 수 있는 게 없다.
        }
    }

    // ── Windows ─────────────────────────────────────────────────────────────
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint wCodePageID);

    private const uint CpUtf8 = 65001;

    private static TerminalMode EnterWindows()
    {
        var hIn = GetStdHandle(StdInputHandle);
        var hOut = GetStdHandle(StdOutputHandle);
        if (!GetConsoleMode(hIn, out var origIn) || !GetConsoleMode(hOut, out var origOut))
        {
            return new TerminalMode(null);
        }

        var newIn = (origIn & ~(EnableLineInput | EnableEchoInput | EnableProcessedInput)) | EnableVirtualTerminalInput;
        var newOut = origOut | EnableVirtualTerminalProcessing;
        SetConsoleMode(hIn, newIn);
        SetConsoleMode(hOut, newOut);

        // 입력 코드페이지를 UTF-8 로. 안 그러면 콘솔이 타이핑/붙여넣기를 OEM 코드페이지(한국어=CP949)
        // 바이트로 넘겨, UTF-8 로 디코딩하는 VtParser 에서 한글 등 비-ASCII 가 깨진다(영어는 무사).
        var origCp = GetConsoleCP();
        if (origCp != CpUtf8)
        {
            SetConsoleCP(CpUtf8);
        }

        return new TerminalMode(() =>
        {
            SetConsoleMode(hIn, origIn);
            SetConsoleMode(hOut, origOut);
            if (origCp != CpUtf8)
            {
                SetConsoleCP(origCp);
            }
        });
    }

    // ── Unix (stty) ─────────────────────────────────────────────────────────
    private static TerminalMode EnterUnix()
    {
        var saved = Stty("-g")?.Trim();
        if (string.IsNullOrEmpty(saved))
        {
            return new TerminalMode(null); // tty 아님/ stty 없음 — degrade
        }

        // raw-ish: 정규화·에코·시그널 끔, 1바이트씩 즉시 읽기.
        Stty("-echo -icanon -isig min 1 time 0");

        return new TerminalMode(() => Stty(saved!));
    }

    // stty 를 /dev/tty 에 대해 실행(리다이렉트된 stdin 이 아니라 실제 단말 제어).
    private static string? Stty(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh", $"-c \"stty {args} < /dev/tty\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return p.ExitCode == 0 ? stdout : null;
        }
        catch
        {
            return null;
        }
    }
}
