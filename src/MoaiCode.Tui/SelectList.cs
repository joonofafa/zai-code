using System.Text;
using MoaiCode.Localization;

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

    // 이번 Prompt 가 제목 줄을 그렸는지(선택 확정 시 위젯 전체를 정확히 지우기 위해 줄 수 계산에 사용).
    private static bool _titleShown;

    /// <summary>
    /// 선택한 인덱스. 취소/비대화형이면 -1. onDelete 를 주면 Del 키로 항목 삭제 가능:
    /// 1차 Del 은 그 행을 밝은 빨강 + 제목 줄임 + "[Del 재입력 시 삭제]"로 무장, 2차 Del 이면
    /// onDelete(index) 호출 후 목록에서 제거한다(호출측 병렬 컬렉션도 같은 index 를 지워 정합 유지).
    /// </summary>
    public static int Prompt(string title, IReadOnlyList<string> items, int defaultIndex = 0, int numberWidth = 1, Func<int, bool>? onDelete = null)
    {
        if (items.Count == 0 || Console.IsInputRedirected)
        {
            return -1;
        }

        _numberWidth = Math.Max(1, numberWidth);
        var list = items.ToList();   // 삭제로 변형될 수 있으므로 로컬 가변 사본으로 다룬다.
        var idx = Math.Clamp(defaultIndex, 0, list.Count - 1);
        var pendingDelete = -1;      // Del 1차로 무장된 행(2차 Del 이면 삭제). onDelete 없으면 미사용.

        // 제목·도움말을 각각 '정확히 1 물리줄'로 clip 한다(좁은 창에서 wrap 되면 선택 확정 시
        // 위젯을 지울 줄 수가 어긋나 중복 잔상이 남던 원인 — 항목과 동일 기준으로 폭 제한).
        _titleShown = false;
        if (!string.IsNullOrEmpty(title))
        {
            Console.WriteLine(Clip(OneLine(title), SafeWidth() - 1));
            _titleShown = true;
        }

        var more = list.Count > MaxVisible ? L10n.Get("common.select.more", list.Count) : "";
        var helpText = L10n.Get("common.select.help", more);
        if (onDelete is not null)
        {
            helpText += L10n.Get("common.select.deleteHint");
        }

        var help = Clip(OneLine(helpText), SafeWidth() - 1);
        Console.WriteLine($"\x1b[38;5;250m{help}\x1b[0m");

        using (ConsolePrompt.Begin())
        {
            Render(list, idx, first: true, pendingDelete);

            while (true)
            {
                var key = (Input.TerminalInput.Shared is { } __ti ? __ti.ReadKey() : Console.ReadKey(intercept: true));
                if (key.KeyChar == '\r' || key.KeyChar == '\n')
                {
                    return Finish(list, idx);
                }

                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        pendingDelete = -1;
                        idx = (idx - 1 + list.Count) % list.Count;
                        Render(list, idx, first: false, pendingDelete);
                        break;
                    case ConsoleKey.DownArrow:
                        pendingDelete = -1;
                        idx = (idx + 1) % list.Count;
                        Render(list, idx, first: false, pendingDelete);
                        break;
                    case ConsoleKey.Enter:
                        return Finish(list, idx);
                    case ConsoleKey.Delete:
                        if (onDelete is null)
                        {
                            break;
                        }

                        if (pendingDelete == idx)
                        {
                            // 2차 Del: 실제 삭제. 호출측 콜백이 병렬 컬렉션도 같은 index 를 제거한다.
                            var oldVisible = Math.Min(list.Count, MaxVisible);
                            if (onDelete(idx))
                            {
                                list.RemoveAt(idx);
                                pendingDelete = -1;
                                if (list.Count == 0)
                                {
                                    ClearWidget(oldVisible);
                                    return -1;
                                }

                                if (idx >= list.Count)
                                {
                                    idx = list.Count - 1;
                                }

                                RedrawAfterDelete(oldVisible, list, idx);
                            }
                            else
                            {
                                pendingDelete = -1;
                                Render(list, idx, first: false, pendingDelete);
                            }
                        }
                        else
                        {
                            // 1차 Del: 이 행을 무장(빨강 + 안내).
                            pendingDelete = idx;
                            Render(list, idx, first: false, pendingDelete);
                        }

                        break;
                    case ConsoleKey.Escape:
                        // 취소 메시지는 호출측이 맥락에 맞게 출력한다(중복 방지 — SessionPicker/model/effort 등).
                        return -1;
                    default:
                        if (key.KeyChar is 'k')
                        {
                            pendingDelete = -1;
                            idx = (idx - 1 + list.Count) % list.Count;
                            Render(list, idx, first: false, pendingDelete);
                        }
                        else if (key.KeyChar is 'j')
                        {
                            pendingDelete = -1;
                            idx = (idx + 1) % list.Count;
                            Render(list, idx, first: false, pendingDelete);
                        }
                        else if (key.KeyChar is >= '1' and <= '9')
                        {
                            var n = key.KeyChar - '0';
                            if (n <= list.Count)
                            {
                                // 선택 즉시 위젯을 지우므로 강조 재그리기는 불필요(잔상 유발 요소 제거).
                                return Finish(list, n - 1);
                            }
                        }

                        break;
                }
            }
        }
    }

    // 선택 확정: 위젯(제목+도움말+항목 목록) 전체를 지우고 한 줄 확정 표시만 남긴다.
    // 강조 상태를 화면에 유지하지 않으므로 리사이즈/줄바꿈으로 인한 중복 잔상이 원천 차단된다.
    private static int Finish(IReadOnlyList<string> items, int idx)
    {
        if (!Console.IsOutputRedirected)
        {
            // 커서는 마지막 항목 바로 아래 줄에 있다. 위젯 시작(제목 또는 도움말 줄)까지 올라가
            // 화면 끝까지 지운다. 각 줄이 1 물리줄로 보장되므로 줄 수 계산이 정확하다.
            var visible = Math.Min(items.Count, MaxVisible);
            var up = visible + 1 + (_titleShown ? 1 : 0); // 항목 + 도움말 1줄 + 제목(있으면) 1줄
            Console.Write($"\x1b[{up}A\r\x1b[0J");
        }

        Console.WriteLine($"\x1b[36m{L10n.Get("common.select.selected", (idx + 1).ToString().PadLeft(_numberWidth, '0'), items[idx])}\x1b[0m");
        return idx;
    }

    private const int MaxVisible = 12;

    private static void Render(IReadOnlyList<string> items, int idx, bool first, int pendingDelete = -1)
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
            var num = (i + 1).ToString().PadLeft(_numberWidth, '0');
            // 위/아래 더 있으면 ↑/↓ 표식, 선택 항목은 ❯ + cyan.
            var scroll = (row == 0 && offset > 0) ? "↑" : (row == visible - 1 && offset + visible < items.Count) ? "↓" : " ";

            // 삭제 무장 행: 밝은 빨강 + 제목을 줄여(…) 삭제 확인 안내 자리를 확보한다.
            if (i == pendingDelete)
            {
                var hint = " " + L10n.Get("session.delete.confirm");
                var head = Clip($"❯ {num}. {OneLine(items[i])}", Math.Max(1, SafeWidth() - 1 - Width(hint)));
                Console.WriteLine($"\x1b[2K\x1b[91m{head}{hint}\x1b[0m");
                continue;
            }

            var selected = i == idx;
            var marker = selected ? "❯" : scroll;

            // 핵심: 각 항목을 '정확히 1 물리줄'로 만든다. 항목 텍스트의 개행 제거 + 터미널 폭(한글 2칸)으로
            // 잘라내야, 재그리기 시 커서 위로-이동(\x1b[{visible}A) 줄 수와 실제 렌더 줄 수가 일치한다.
            // (긴/여러줄 항목이 wrap 되면 줄 수가 어긋나 화면에 중첩 표시되던 버그 수정.)
            var plain = Clip($"{marker} {num}. {OneLine(items[i])}", SafeWidth() - 1);
            var line = selected ? $"\x1b[36m{plain}\x1b[0m" : plain;
            Console.WriteLine($"\x1b[2K{line}");
        }
    }

    // 삭제로 항목 수가 줄면, 기존 항목 영역을 첫 줄부터 지우고(줄 수 감소 잔상 방지) 새 목록으로 다시 그린다.
    private static void RedrawAfterDelete(int oldVisible, IReadOnlyList<string> items, int idx)
    {
        if (!Console.IsOutputRedirected)
        {
            Console.Write($"\x1b[{oldVisible}A\r\x1b[0J");
        }

        Render(items, idx, first: true);
    }

    // 마지막 항목까지 삭제됨: 위젯(제목+도움말+항목) 전체를 지운다.
    private static void ClearWidget(int oldVisible)
    {
        if (!Console.IsOutputRedirected)
        {
            var up = oldVisible + 1 + (_titleShown ? 1 : 0); // 항목 + 도움말 1줄 + 제목(있으면) 1줄
            Console.Write($"\x1b[{up}A\r\x1b[0J");
        }
    }

    // 표시 폭(한글/CJK=2칸) 합. 무장 행에서 안내 문구 자리 확보용.
    internal static int Width(string s)
    {
        var w = 0;
        foreach (var c in s)
        {
            w += IsWide(c) ? 2 : 1;
        }

        return w;
    }

    // internal: 멀티셀렉트(MultiSelectList)와 렌더 헬퍼를 공유한다.
    internal static int SafeWidth()
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

    internal static string OneLine(string s)
        => s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    // 표시 폭(한글/CJK=2칸) 기준으로 maxWidth 이내로 자르고, 잘리면 … 를 붙인다.
    internal static string Clip(string s, int maxWidth)
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
