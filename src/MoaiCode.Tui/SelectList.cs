using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// 화살표 키로 고르는 동기식 선택 위젯. Spectre SelectionPrompt(단일 파일에서 TypeConverter 크래시)
/// 대신 raw ANSI + Console.ReadKey 로 구현. 호출 동안 입력을 단독 점유하므로 백그라운드 reader와
/// 경합하지 않는다(스피너는 ConsolePrompt 로 일시정지). ↑/↓(또는 j/k) 이동, Enter 선택, Esc 취소,
/// 숫자키 즉시 선택.
/// </summary>
public static class SelectList
{
    // 항목 번호 자리폭(0채움). 세션 목록 등에서 2자리(01.)로 줄맞춤하려고 호출별로 설정.
    // 위젯은 콘솔 입력을 단독·동기 점유하므로 한 번에 하나의 Prompt만 활성 → 필드로 충분.
    private static int _numberWidth = 1;

    /// <summary>선택한 인덱스. 취소/비대화형이면 -1.</summary>
    public static int Prompt(string title, IReadOnlyList<string> items, int defaultIndex = 0, int numberWidth = 1)
    {
        if (items.Count == 0 || Console.IsInputRedirected)
        {
            return -1;
        }

        _numberWidth = Math.Max(1, numberWidth);
        var idx = Math.Clamp(defaultIndex, 0, items.Count - 1);

        if (!string.IsNullOrEmpty(title))
        {
            Console.WriteLine(title);
        }

        var more = items.Count > MaxVisible ? $" · 총 {items.Count}개" : "";
        Console.WriteLine($"\x1b[38;5;250m(↑/↓ 이동 · Enter 선택 · Esc 취소 · 숫자 즉시선택{more})\x1b[0m");

        using (ConsolePrompt.Begin())
        {
            Render(items, idx, first: true);

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.KeyChar == '\r' || key.KeyChar == '\n')
                {
                    return Finish(items, idx);
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        idx = (idx - 1 + items.Count) % items.Count;
                        Render(items, idx, first: false);
                        break;
                    case ConsoleKey.DownArrow:
                        idx = (idx + 1) % items.Count;
                        Render(items, idx, first: false);
                        break;
                    case ConsoleKey.Enter:
                        return Finish(items, idx);
                    case ConsoleKey.Escape:
                        Console.WriteLine("\x1b[38;5;250m  (취소됨)\x1b[0m");
                        return -1;
                    default:
                        if (key.KeyChar is 'k')
                        {
                            idx = (idx - 1 + items.Count) % items.Count;
                            Render(items, idx, first: false);
                        }
                        else if (key.KeyChar is 'j')
                        {
                            idx = (idx + 1) % items.Count;
                            Render(items, idx, first: false);
                        }
                        else if (key.KeyChar is >= '1' and <= '9')
                        {
                            var n = key.KeyChar - '0';
                            if (n <= items.Count)
                            {
                                // 숫자 선택 시에도 최종 강조를 반영(stale 방지) 후 확정.
                                idx = n - 1;
                                Render(items, idx, first: false);
                                return Finish(items, idx);
                            }
                        }

                        break;
                }
            }
        }
    }

    // 선택 확정: 최종 강조 줄 아래에 무엇을 골랐는지 명확히 표시 (재그리기 글리치와 무관하게 분명).
    private static int Finish(IReadOnlyList<string> items, int idx)
    {
        Console.WriteLine($"\x1b[36m  ✓ 선택: {(idx + 1).ToString().PadLeft(_numberWidth, '0')}. {items[idx]}\x1b[0m");
        return idx;
    }

    private const int MaxVisible = 12;

    private static void Render(IReadOnlyList<string> items, int idx, bool first)
    {
        // 뷰포트: 항목이 많아도 항상 고정 높이(min(count, MaxVisible))만 그려 커서 계산을 안정화.
        var visible = Math.Min(items.Count, MaxVisible);
        if (!first)
        {
            Console.Write($"\x1b[{visible}A");
        }

        var offset = items.Count <= visible
            ? 0
            : Math.Clamp(idx - visible / 2, 0, items.Count - visible);

        for (var row = 0; row < visible; row++)
        {
            var i = offset + row;
            var selected = i == idx;
            var num = (i + 1).ToString().PadLeft(_numberWidth, '0');
            // 위/아래 더 있으면 ↑/↓ 표식, 선택 항목은 ❯ + cyan.
            var scroll = (row == 0 && offset > 0) ? "↑" : (row == visible - 1 && offset + visible < items.Count) ? "↓" : " ";
            var marker = selected ? "❯" : scroll;

            // 핵심: 각 항목을 '정확히 1 물리줄'로 만든다. 항목 텍스트의 개행 제거 + 터미널 폭(한글 2칸)으로
            // 잘라내야, 재그리기 시 커서 위로-이동(\x1b[{visible}A) 줄 수와 실제 렌더 줄 수가 일치한다.
            // (긴/여러줄 항목이 wrap 되면 줄 수가 어긋나 화면에 중첩 표시되던 버그 수정.)
            var plain = Clip($"{marker} {num}. {OneLine(items[i])}", SafeWidth() - 1);
            var line = selected ? $"\x1b[36m{plain}\x1b[0m" : plain;
            Console.WriteLine($"\x1b[2K{line}");
        }
    }

    private static int SafeWidth()
    {
        try
        {
            var w = Console.WindowWidth;
            return w > 10 ? w : 80;
        }
        catch
        {
            return 80;
        }
    }

    private static string OneLine(string s)
        => s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    // 표시 폭(한글/CJK=2칸) 기준으로 maxWidth 이내로 자르고, 잘리면 … 를 붙인다.
    private static string Clip(string s, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        var w = 0;
        var truncated = false;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var cw = IsWide(c) ? 2 : 1;
            if (w + cw > maxWidth)
            {
                truncated = true;
                break;
            }

            sb.Append(c);
            w += cw;
        }

        if (truncated)
        {
            while (w + 1 > maxWidth && sb.Length > 0)
            {
                w -= IsWide(sb[^1]) ? 2 : 1;
                sb.Length--;
            }

            sb.Append('…');
        }

        return sb.ToString();
    }

    // 한글/CJK 등 전각 문자는 터미널에서 2칸을 차지 (LineEditor.IsWide 와 동일 기준).
    private static bool IsWide(char c) =>
        (c >= 'ᄀ' && c <= 'ᅟ') ||
        (c >= '⺀' && c <= '꓏') ||
        (c >= '가' && c <= '힣') ||
        (c >= '豈' && c <= '﫿') ||
        (c >= '︰' && c <= '﹏') ||
        (c >= '＀' && c <= '｠') ||
        (c >= '￠' && c <= '￦');
}
