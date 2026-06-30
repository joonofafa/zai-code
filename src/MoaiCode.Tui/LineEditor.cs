using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// raw 모드 한 줄 입력기. Console.ReadLine 대신 키 단위로 읽어 명령 히스토리(↑/↓),
/// 슬래시 자동완성(Tab), 커서 이동/편집(←/→/Home/End/Backspace)을 지원한다.
/// 비대화형(파이프/리다이렉트)에서는 Console.ReadLine 으로 폴백.
/// 주의: raw 모드라 한글 IME 조합 중간 표시는 터미널에 따라 어색할 수 있다(최종 문자는 정상 입력됨).
/// </summary>
public static class LineEditor
{
    private const string PromptText = "❯ ";

    /// <summary>Shift+Tab 입력 시 반환되는 신호 (호출측이 모드 토글 처리).</summary>
    public const string CycleModeSignal = "__cycle_mode__";

    /// <summary>
    /// 한 줄을 읽어 반환. EOF(Ctrl+D, 빈 줄)면 null.
    /// cycleMode 가 주어지면 Shift+Tab 시 그것을 호출(모드 토글)하고, 반환된 상태줄 문자열로
    /// 바로 위 줄(상태바)을 '제자리' 갱신한다(새 줄을 찍지 않음).
    /// </summary>
    public static string? ReadLine(
        IReadOnlyList<string> history,
        IReadOnlyList<string> slashCommands,
        Func<string>? cycleMode = null,
        Func<string>? statusLine = null)
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(PromptText);
            return Console.ReadLine();
        }

        var buf = new StringBuilder();
        var pos = 0;
        var histIdx = history.Count; // == count → 새 입력(편집 중)
        var savedCurrent = "";

        // boxed: 입력창 위/아래 가로선 + 그 아래 상태줄 (Claude/Codex 스타일).
        var boxed = statusLine is not null;
        if (boxed)
        {
            DrawChrome(buf, pos, statusLine!());
        }
        else
        {
            Redraw(buf, pos);
        }

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            // Enter: 실제 터미널은 \r, 일부 환경/파이프는 \n → 둘 다 처리.
            if (key.Key == ConsoleKey.Enter || key.KeyChar == '\r' || key.KeyChar == '\n')
            {
                if (boxed)
                {
                    MoveBelowBox();
                }
                else
                {
                    Console.WriteLine();
                }

                return buf.ToString();
            }

            switch (key.Key)
            {

                case ConsoleKey.Backspace:
                    if (pos > 0)
                    {
                        buf.Remove(pos - 1, 1);
                        pos--;
                        Redraw(buf, pos);
                    }

                    break;

                case ConsoleKey.Delete:
                    if (pos < buf.Length)
                    {
                        buf.Remove(pos, 1);
                        Redraw(buf, pos);
                    }

                    break;

                case ConsoleKey.LeftArrow:
                    if (pos > 0)
                    {
                        pos--;
                        Redraw(buf, pos);
                    }

                    break;

                case ConsoleKey.RightArrow:
                    if (pos < buf.Length)
                    {
                        pos++;
                        Redraw(buf, pos);
                    }

                    break;

                case ConsoleKey.Home:
                    pos = 0;
                    Redraw(buf, pos);
                    break;

                case ConsoleKey.End:
                    pos = buf.Length;
                    Redraw(buf, pos);
                    break;

                case ConsoleKey.UpArrow:
                    if (histIdx > 0)
                    {
                        if (histIdx == history.Count)
                        {
                            savedCurrent = buf.ToString();
                        }

                        histIdx--;
                        SetBuffer(buf, ref pos, history[histIdx]);
                        Redraw(buf, pos);
                    }

                    break;

                case ConsoleKey.DownArrow:
                    if (histIdx < history.Count)
                    {
                        histIdx++;
                        SetBuffer(buf, ref pos, histIdx == history.Count ? savedCurrent : history[histIdx]);
                        Redraw(buf, pos);
                    }

                    break;

                case ConsoleKey.Tab:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        if (cycleMode is not null)
                        {
                            var status = cycleMode();
                            if (boxed)
                            {
                                // 상태줄은 프롬프트 2줄 아래(하단선+상태). 거기만 제자리 교체.
                                Console.Write("\x1b[2B\r\x1b[2K" + status + "\x1b[2A");
                            }
                            else
                            {
                                // 비박스: 상태줄이 프롬프트 1줄 위.
                                Console.Write("\x1b[1A\r\x1b[2K" + status + "\x1b[1B");
                            }

                            Redraw(buf, pos);
                            break;
                        }

                        Console.WriteLine();
                        return CycleModeSignal; // 폴백: 호출측이 처리
                    }

                    if (TryComplete(buf, ref pos, slashCommands))
                    {
                        Redraw(buf, pos);
                    }

                    break;

                default:
                    // Ctrl+D: 빈 줄이면 EOF.
                    if (key.KeyChar == '\u0004')
                    {
                        if (buf.Length == 0)
                        {
                            if (boxed)
                            {
                                MoveBelowBox();
                            }
                            else
                            {
                                Console.WriteLine();
                            }

                            return null;
                        }

                        break;
                    }

                    // Ctrl+U: 줄 비우기.
                    if (key.KeyChar == '\u0015')
                    {
                        buf.Clear();
                        pos = 0;
                        Redraw(buf, pos);
                        break;
                    }

                    // 일반 문자(제어문자 제외) 삽입.
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Insert(pos, key.KeyChar);
                        pos++;
                        Redraw(buf, pos);
                    }

                    break;
            }
        }
    }

    private static void SetBuffer(StringBuilder buf, ref int pos, string text)
    {
        buf.Clear();
        buf.Append(text);
        pos = buf.Length;
    }

    // 슬래시 명령 자동완성. /pre → 매칭 1개면 완성, 여러 개면 공통 접두까지 + 목록 표시.
    private static bool TryComplete(StringBuilder buf, ref int pos, IReadOnlyList<string> slashCommands)
    {
        var text = buf.ToString();
        if (!text.StartsWith('/') || text.Contains(' '))
        {
            return false;
        }

        var token = text[1..];
        var matches = slashCommands
            .Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
        if (matches.Count == 0)
        {
            return false;
        }

        if (matches.Count == 1)
        {
            SetBuffer(buf, ref pos, "/" + matches[0] + " ");
            return true;
        }

        var common = LongestCommonPrefix(matches);
        if (common.Length > token.Length)
        {
            SetBuffer(buf, ref pos, "/" + common);
            return true;
        }

        // 더 못 좁히면 후보 목록을 한 번 출력하고 입력을 다시 그린다.
        Console.WriteLine();
        Console.WriteLine("\x1b[38;5;250m  " + string.Join("   ", matches.Select(m => "/" + m)) + "\x1b[0m");
        return true;
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return "";
        }

        var prefix = items[0];
        foreach (var s in items)
        {
            var n = 0;
            while (n < prefix.Length && n < s.Length
                   && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(s[n]))
            {
                n++;
            }

            prefix = prefix[..n];
            if (prefix.Length == 0)
            {
                break;
            }
        }

        return prefix;
    }

    // boxed 초기 그리기: [상단선] / 프롬프트(편집줄) / [하단선] / 상태줄. 커서는 프롬프트 줄로 복귀.
    private static void DrawChrome(StringBuilder buf, int pos, string status)
    {
        var rule = DimRule();
        Console.WriteLine(rule);                          // 상단 가로선
        Redraw(buf, pos);                                 // 프롬프트(편집) 줄
        Console.Write("\n" + rule + "\n" + status);       // 2줄 아래: 하단선 + 상태줄
        Console.Write("\x1b[2A");                          // 프롬프트 줄로 복귀(위로 2줄)
        Redraw(buf, pos);                                  // 커서 위치 보정
    }

    // boxed 종료(Enter/EOF): 박스 아래(상태줄 다음)로 커서를 옮겨 이후 출력이 박스 밑에 오게 한다.
    private static void MoveBelowBox()
        => Console.Write("\x1b[2B\r\n");

    private static string DimRule()
    {
        int width;
        try
        {
            width = Console.WindowWidth;
        }
        catch
        {
            width = 80;
        }

        width = Math.Clamp(width, 20, 200);
        return "\x1b[38;5;240m" + new string('─', width) + "\x1b[0m";
    }

    private static void Redraw(StringBuilder buf, int pos)
    {
        // 줄 전체 지우고 프롬프트+버퍼 출력, 커서를 pos(표시폭 기준)로 이동.
        Console.Write("\r\x1b[K");
        Console.Write("\x1b[32m" + PromptText + "\x1b[0m");
        Console.Write(buf.ToString());
        Console.Write("\r");
        var col = DisplayWidth(PromptText) + DisplayWidth(buf.ToString(0, pos));
        if (col > 0)
        {
            Console.Write($"\x1b[{col}C");
        }
    }

    private static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var c in s)
        {
            w += IsWide(c) ? 2 : 1;
        }

        return w;
    }

    // 한글/CJK 등 전각 문자는 터미널에서 2칸을 차지 → 커서 계산에 반영.
    private static bool IsWide(char c) =>
        (c >= 'ᄀ' && c <= 'ᅟ') ||   // Hangul Jamo
        (c >= '⺀' && c <= '꓏') ||   // CJK Radicals .. Yi
        (c >= '가' && c <= '힣') ||   // Hangul Syllables
        (c >= '豈' && c <= '﫿') ||   // CJK Compatibility Ideographs
        (c >= '︰' && c <= '﹏') ||   // CJK Compatibility Forms
        (c >= '＀' && c <= '｠') ||   // Fullwidth Forms
        (c >= '￠' && c <= '￦');
}
