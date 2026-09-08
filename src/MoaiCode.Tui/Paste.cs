using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MoaiCode.Localization;
using MoaiCode.Tui.Input;

namespace MoaiCode.Tui;

/// <summary>
/// 입력 가용성 조회의 얇은 파사드. 붙여넣기(bracketed paste) 파싱·감지는 이제 raw 바이트 리더
/// (TerminalInput + VtParser)가, VT 기능 켬/끔은 TerminalSession 이 담당한다.
/// (예전엔 이 클래스가 ESC[200~ 를 키 레벨에서 스캔했으나 입력 층 재작성으로 제거됨.)
/// </summary>
public static class BracketedPaste
{
    /// <summary>대기 중인 입력이 있는가. 세션(raw 리더)이 있으면 그 큐, 없으면 Console 폴백.</summary>
    public static bool KeyAvailable
        => TerminalInput.Shared is { } input ? input.Available : Console.KeyAvailable;
}

/// <summary>
/// 여러 줄 붙여넣기를 입력창에서는 한 줄짜리 표식으로 접어 두고, 모델에 보낼 때 원문으로 되돌린다.
/// 하단 고정 도크가 예약하는 행수를 일정하게 유지하기 위함(로그 100줄을 붙여도 입력창은 한 줄).
/// 표식은 세션 동안 유지되므로 히스토리(↑)로 불러온 표식도 그대로 확장된다.
/// </summary>
public static class PasteStore
{
    private static readonly Dictionary<int, string> Items = new();
    // 토큰은 표시용으로 로컬라이즈되므로(ko: "…줄]", en: "… lines]") 정규식은 두 언어를 모두 매칭한다.
    // 세션 중 언어가 바뀌어도(또는 히스토리에 다른 언어 토큰이 있어도) 안전하게 확장된다.
    private static readonly Regex Token = new(@"\[(?:붙여넣기|Paste) #(\d+) · (\d+)(?:줄| lines)\]", RegexOptions.Compiled);
    private static int _next = 1;

    /// <summary>붙여넣은 원문을 보관하고 표식 문자열을 돌려준다.</summary>
    public static string Placeholder(string text)
    {
        var id = _next++;
        Items[id] = text;
        var lines = text.Count(c => c == '\n') + 1;
        return L10n.Get("paste.token", id, lines);
    }

    /// <summary>표식을 원문으로 되돌린다. 보관되지 않은 표식(다른 세션 등)은 그대로 둔다.</summary>
    public static string Expand(string input) =>
        (input.Contains("[붙여넣기 #", StringComparison.Ordinal) || input.Contains("[Paste #", StringComparison.Ordinal))
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
