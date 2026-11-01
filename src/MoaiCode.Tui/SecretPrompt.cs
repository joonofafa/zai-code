using System.Text;
using MoaiCode.Tui.Input;

namespace MoaiCode.Tui;

/// <summary>API 키 표시용 마스킹. 앞 <see cref="RevealChars"/>자만 보이고 나머지는 '*'.</summary>
public static class SecretMask
{
    public const int RevealChars = 8;

    // 별표는 이만큼까지만 찍고 나머지는 길이로 보여 준다 — 100자 넘는 키가 줄을 넘겨 다시 그리기가 깨지지 않게.
    private const int MaxStars = 24;

    /// <summary>앞 8자 + '*'. 8자 이하는 전부 가린다(짧은 값이 통째로 드러나지 않게).</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return string.Empty;
        }

        if (secret.Length <= RevealChars)
        {
            return new string('*', secret.Length);
        }

        var rest = secret.Length - RevealChars;
        return secret[..RevealChars]
               + new string('*', Math.Min(rest, MaxStars))
               + (rest > MaxStars ? $" ({secret.Length})" : string.Empty);
    }
}

/// <summary>
/// API 키 입력. 붙여넣기(bracketed paste)·타이핑 모두 받고, 입력 중에도 <see cref="SecretMask"/> 형태로만 보여 준다.
/// Enter 확정, Esc/Ctrl+C 는 취소(null). 비대화형이면 한 줄을 그대로 읽는다(테스트·파이프).
/// </summary>
public static class SecretPrompt
{
    public static string? Read(string label)
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(label);
            return Console.ReadLine()?.Trim();
        }

        var input = TerminalInput.Shared;
        using (ConsolePrompt.Begin())
        {
            return Read(label, () => input is not null ? input.ReadEvent() : new KeyEvent(Console.ReadKey(intercept: true)), Console.Write);
        }
    }

    /// <summary>입력 이벤트 원천과 출력을 주입받는 본체(테스트가 쓴다).</summary>
    internal static string? Read(string label, Func<InputEvent?> next, Action<string> write)
    {
        var sb = new StringBuilder();
        void Render() => write("\r\u001b[2K" + label + SecretMask.Mask(sb.ToString()));

        Render();
        while (true)
        {
            switch (next())
            {
                case null:
                case CancelEvent:
                    write("\n");
                    return null;
                case PasteEvent p:
                    // 키에는 공백·개행이 없다 — 복사할 때 딸려 온 줄바꿈·공백은 버린다.
                    foreach (var ch in p.Text)
                    {
                        if (!char.IsWhiteSpace(ch) && !char.IsControl(ch))
                        {
                            sb.Append(ch);
                        }
                    }

                    Render();
                    break;
                case KeyEvent { Key: var key }:
                    if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
                    {
                        write("\n");
                        return sb.ToString();
                    }

                    if (key.Key == ConsoleKey.Escape)
                    {
                        write("\n");
                        return null;
                    }

                    if (key.Key == ConsoleKey.Backspace || key.KeyChar is '\b' or '\u007f')
                    {
                        if (sb.Length > 0)
                        {
                            sb.Length--;
                        }
                    }
                    else if (!char.IsControl(key.KeyChar) && !char.IsWhiteSpace(key.KeyChar))
                    {
                        sb.Append(key.KeyChar);
                    }

                    Render();
                    break;
            }
        }
    }
}
