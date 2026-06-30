using System.Text;

namespace MoaiCode.Tui;

/// <summary>비밀번호 마스킹 입력 (에코 없이 *). 비대화형이면 평문 ReadLine 폴백(테스트/파이프).</summary>
public static class PasswordPrompt
{
    public static string Read(string label)
    {
        Console.Write(label);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter || k.KeyChar == '\r' || k.KeyChar == '\n')
            {
                Console.WriteLine();
                break;
            }

            if (k.Key == ConsoleKey.Backspace || k.KeyChar == '\b' || k.KeyChar == '\u007f')
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(k.KeyChar))
            {
                sb.Append(k.KeyChar);
                Console.Write('*');
            }
        }

        return sb.ToString();
    }
}
