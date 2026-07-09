using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MoaiCode.Tui;

/// <summary>
/// bracketed paste (DEC 2004) 처리. 켜두면 터미널이 붙여넣기를 ESC[200~ … ESC[201~ 로 감싸 보내므로
/// 붙여넣은 개행을 Enter(제출)와 구분할 수 있다. 켜지 않으면 터미널이 붙여넣기를 평범한 키 입력으로
/// 흘려보내고, CRLF 가 Enter 두 번으로 들어와 줄마다 전송돼 버린다.
/// </summary>
public static class BracketedPaste
{
    public const string Enable = "\u001b[?2004h";
    public const string Disable = "\u001b[?2004l";

    private const string StartTail = "[200~";  // ESC 다음
    private const string EndTail = "[201~";    // ESC 다음
    private const int MaxPasteChars = 4_000_000; // 종료 마커가 안 오는 터미널에서 무한 대기 방지

    // 시퀀스가 아니었을 때 되돌려 놓기 위한 pushback 큐(선행 읽기 취소용).
    private static readonly Queue<ConsoleKeyInfo> Pushback = new();

    public static ConsoleKeyInfo ReadKey() =>
        Pushback.Count > 0 ? Pushback.Dequeue() : Console.ReadKey(intercept: true);

    /// <summary>
    /// ESC 로 시작하는 키가 붙여넣기 시작(ESC[200~)이면 종료 마커까지 본문을 읽어 반환한다.
    /// 아니면 선행 읽은 키를 모두 pushback 하고 false.
    /// </summary>
    public static bool TryReadPaste(ConsoleKeyInfo first, out string text)
    {
        text = string.Empty;

        // 단독 ESC(사용자가 Esc 를 누른 경우)에 블로킹하지 않도록, 뒤따르는 입력이 있을 때만 시도.
        if (first.Key != ConsoleKey.Escape || (Pushback.Count == 0 && !Console.KeyAvailable))
        {
            return false;
        }

        var consumed = new List<ConsoleKeyInfo>();
        foreach (var expect in StartTail)
        {
            var k = ReadKey();
            consumed.Add(k);
            if (k.KeyChar != expect)
            {
                foreach (var c in consumed)
                {
                    Pushback.Enqueue(c);
                }

                return false;
            }
        }

        var sb = new StringBuilder();
        while (sb.Length < MaxPasteChars)
        {
            var k = ReadKey();

            if (k.Key == ConsoleKey.Escape)
            {
                // ESC[201~ 이면 종료. 아니면 읽은 만큼 본문으로 취급.
                var seq = new List<ConsoleKeyInfo>();
                var matched = true;
                foreach (var expect in EndTail)
                {
                    var e = ReadKey();
                    seq.Add(e);
                    if (e.KeyChar != expect)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    break;
                }

                sb.Append('\u001b');
                foreach (var s in seq)
                {
                    sb.Append(s.KeyChar);
                }

                continue;
            }

            sb.Append(k.KeyChar);
        }

        text = sb.ToString();
        return true;
    }
}

/// <summary>
/// 여러 줄 붙여넣기를 입력창에서는 한 줄짜리 표식으로 접어 두고, 모델에 보낼 때 원문으로 되돌린다.
/// 하단 고정 도크가 예약하는 행수를 일정하게 유지하기 위함(로그 100줄을 붙여도 입력창은 한 줄).
/// 표식은 세션 동안 유지되므로 히스토리(↑)로 불러온 표식도 그대로 확장된다.
/// </summary>
public static class PasteStore
{
    private static readonly Dictionary<int, string> Items = new();
    private static readonly Regex Token = new(@"\[붙여넣기 #(\d+) · (\d+)줄\]", RegexOptions.Compiled);
    private static int _next = 1;

    /// <summary>붙여넣은 원문을 보관하고 표식 문자열을 돌려준다.</summary>
    public static string Placeholder(string text)
    {
        var id = _next++;
        Items[id] = text;
        var lines = text.Count(c => c == '\n') + 1;
        return $"[붙여넣기 #{id} · {lines}줄]";
    }

    /// <summary>표식을 원문으로 되돌린다. 보관되지 않은 표식(다른 세션 등)은 그대로 둔다.</summary>
    public static string Expand(string input) =>
        input.Contains("[붙여넣기 #", StringComparison.Ordinal)
            ? Token.Replace(input, m =>
                int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                && Items.TryGetValue(id, out var t)
                    ? t
                    : m.Value)
            : input;

    /// <summary>커서 바로 앞이 표식이면 그 길이(통째로 지우기 위함), 아니면 0.</summary>
    public static int PlaceholderLengthEndingAt(string buf, int pos)
    {
        if (pos <= 0 || pos > buf.Length || buf[pos - 1] != ']')
        {
            return 0;
        }

        var open = buf.LastIndexOf('[', pos - 1);
        if (open < 0)
        {
            return 0;
        }

        var span = buf[open..pos];
        var m = Token.Match(span);
        return m.Success && m.Index == 0 && m.Length == span.Length ? span.Length : 0;
    }

    /// <summary>
    /// 붙여넣은 텍스트를 편집 버퍼에 삽입한다. 개행이 남아 있으면 표식으로 접는다.
    /// 터미널에서 한 줄만 복사하면 끝에 개행이 붙어 오므로, 후행 개행은 먼저 떼어낸다.
    /// </summary>
    public static void Insert(StringBuilder buf, ref int pos, string pasted)
    {
        var text = pasted.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
        if (text.Length == 0)
        {
            return;
        }

        var insert = text.Contains('\n') ? Placeholder(text) : text;
        buf.Insert(pos, insert);
        pos += insert.Length;
    }
}
