using Spectre.Console;

namespace MoaiCode.Tui.Input;

/// <summary>
/// 대화형 입력 세션(raw 모드)이 활성이면 공용 리더로, 아니면 기존 Console/Spectre 로 라우팅하는 헬퍼.
/// 로그인·프록시·설정·질문 툴처럼 REPL 세션 중에도, 세션 밖(로그인 전)에도 불릴 수 있는 라인 입력에 쓴다.
/// (raw 모드에서 Console.ReadLine 을 그대로 부르면 리더와 stdin 을 다투므로 반드시 이 헬퍼를 경유한다.)
/// </summary>
public static class InputCompat
{
    /// <summary>한 줄 입력(에코). 세션이 있으면 리더로, 없으면 Console.ReadLine.</summary>
    public static string? ReadLine()
        => TerminalInput.Shared is { } input ? input.ReadLine() : Console.ReadLine();

    /// <summary>비밀번호 한 줄(마스킹). 세션이 있으면 리더로, 없으면 호출자가 별도 처리(null 반환).</summary>
    public static string? ReadPassword()
        => TerminalInput.Shared is { } input ? input.ReadLine(mask: true) : null;

    /// <summary>예/아니오 확인. 세션이 있으면 리더 한 글자로, 없으면 Spectre Confirm.</summary>
    public static bool Confirm(string prompt, bool defaultValue)
    {
        if (TerminalInput.Shared is not { } input)
        {
            return AnsiConsole.Confirm(prompt, defaultValue);
        }

        AnsiConsole.Markup($"{Markup.Escape(prompt)} [grey][[{(defaultValue ? "Y/n" : "y/N")}]][/] ");
        var line = input.ReadLine()?.Trim();
        AnsiConsole.WriteLine();
        if (string.IsNullOrEmpty(line))
        {
            return defaultValue;
        }

        return line[0] is 'y' or 'Y';
    }
}
