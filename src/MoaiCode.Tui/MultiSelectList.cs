using MoaiCode.Localization;

namespace MoaiCode.Tui;

/// <summary>
/// 체크박스형 다중 선택 위젯 ([x]/[ ]). SelectList 와 같은 raw ANSI 렌더(뷰포트·폭 계산 공유).
/// ↑/↓(j/k) 이동, Space 토글, a 전체 토글, Enter 저장, Esc 취소. 비대화형(리다이렉트)이면 null.
/// </summary>
public static class MultiSelectList
{
    private const int MaxVisible = 12;

    /// <summary>체크된 인덱스 목록. 취소/비대화형이면 null.</summary>
    public static IReadOnlyList<int>? Prompt(
        string title, IReadOnlyList<string> items, IReadOnlyList<bool> initial)
    {
        if (items.Count == 0 || Console.IsInputRedirected)
        {
            return null;
        }

        var chk = new bool[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            chk[i] = i < initial.Count && initial[i];
        }

        var pad = items.Count.ToString().Length;
        var idx = 0;

        if (!string.IsNullOrEmpty(title))
        {
            Console.WriteLine(title);
        }

        Console.WriteLine($"\x1b[38;5;250m{L10n.Get("common.multiselect.help")}\x1b[0m");

        using (ConsolePrompt.Begin())
        {
            Render(items, chk, idx, pad, first: true);

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter || key.KeyChar == '\r' || key.KeyChar == '\n')
                {
                    var result = new List<int>();
                    for (var i = 0; i < chk.Length; i++)
                    {
                        if (chk[i])
                        {
                            result.Add(i);
                        }
                    }

                    Console.WriteLine($"\x1b[36m{L10n.Get("common.multiselect.saved", result.Count, items.Count - result.Count)}\x1b[0m");
                    return result;
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        idx = (idx - 1 + items.Count) % items.Count;
                        Render(items, chk, idx, pad, first: false);
                        break;
                    case ConsoleKey.DownArrow:
                        idx = (idx + 1) % items.Count;
                        Render(items, chk, idx, pad, first: false);
                        break;
                    case ConsoleKey.Spacebar:
                        chk[idx] = !chk[idx];
                        Render(items, chk, idx, pad, first: false);
                        break;
                    case ConsoleKey.Escape:
                        // 취소 안내는 호출측이 상황에 맞게 출력한다(로그인 온보딩 vs /skills).
                        return null;
                    default:
                        if (key.KeyChar is 'k')
                        {
                            idx = (idx - 1 + items.Count) % items.Count;
                            Render(items, chk, idx, pad, first: false);
                        }
                        else if (key.KeyChar is 'j')
                        {
                            idx = (idx + 1) % items.Count;
                            Render(items, chk, idx, pad, first: false);
                        }
                        else if (key.KeyChar is ' ')
                        {
                            chk[idx] = !chk[idx];
                            Render(items, chk, idx, pad, first: false);
                        }
                        else if (key.KeyChar is 'a' or 'A')
                        {
                            var allOn = Array.TrueForAll(chk, b => b);
                            for (var i = 0; i < chk.Length; i++)
                            {
                                chk[i] = !allOn;
                            }

                            Render(items, chk, idx, pad, first: false);
                        }

                        break;
                }
            }
        }
    }

    private static void Render(IReadOnlyList<string> items, bool[] chk, int idx, int pad, bool first)
    {
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
            var num = (i + 1).ToString().PadLeft(pad, '0');
            var scroll = (row == 0 && offset > 0) ? "↑"
                : (row == visible - 1 && offset + visible < items.Count) ? "↓" : " ";
            var marker = selected ? "❯" : scroll;
            var box = chk[i] ? "[x]" : "[ ]";

            // 각 항목을 정확히 1 물리줄로 (개행 제거 + 폭 클립) — 재그리기 커서 계산 안정화.
            var plain = SelectList.Clip(
                $"{marker} {box} {num}. {SelectList.OneLine(items[i])}", SelectList.SafeWidth() - 1);
            var line = selected ? $"\x1b[36m{plain}\x1b[0m" : plain;
            Console.WriteLine($"\x1b[2K{line}");
        }
    }
}
