using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// 하단 고정 상태줄 + 입력창 (opt-in: MOAI_BOTTOM_DOCK=1).
///
/// DECSTBM 스크롤 영역(`\x1b[1;{bottom}r`)으로 화면 위쪽만 스크롤되게 하고, 남은 하단 줄에
/// 상태줄과 입력창을 절대 위치로 그린다. 스트리밍 출력은 위 영역에서 자연스럽게 스크롤되고
/// 입력/상태는 그대로 고정된다(irssi/weechat 방식). 입력 wrap 시 예약 줄 수를 동적으로 조정한다.
///
/// 인라인 LineEditor 는 그대로 두고(리스크 격리), 여기서만 자체 키 루프를 돈다.
/// 입력 wrap 계산·표시폭·프롬프트/배경은 LineEditor 의 internal 헬퍼를 공유한다.
/// </summary>
public sealed class BottomDock
{
    private readonly Func<string> _status;
    private int _reserved;      // 현재 예약된 하단 줄 수(상태 1 + 입력행)
    private bool _installed;    // 스크롤 영역이 설정돼 있는가

    public BottomDock(Func<string> status) => _status = status;

    private static int Height()
    {
        try { var h = Console.WindowHeight; return h < 1 ? 24 : h; }
        catch { return 24; }
    }

    private static int Width()
    {
        try { var w = Console.WindowWidth; return w < 1 ? 80 : w; }
        catch { return 80; }
    }

    /// <summary>하단 고정이 의미 있는 최소 높이(활동/라인/입력/라인/상태 = 최소 5줄 예약 + 스크롤 여유).</summary>
    public static bool Fits() => Height() >= 9;

    /// <summary>스크롤 영역 해제 + 커서를 맨 아래로. REPL 종료/전환 시 반드시 호출.</summary>
    public void Teardown()
    {
        if (!_installed)
        {
            return;
        }

        var h = Height();
        Console.Write($"\x1b[?25h\x1b[r\x1b[{h};1H\r\n"); // 커서 복원 + 영역 해제 + 맨 아래로
        _installed = false;
        _reserved = 0;
    }

    /// <summary>하단 상태줄+입력창을 그린다(현재 입력 버퍼/커서 반영). 입력 wrap 만큼 예약 줄 조정.</summary>
    private void Draw(StringBuilder buf, int pos, string? statusOverride = null)
    {
        int h = Height(), w = Width();
        var plen = LineEditor.DisplayWidth(LineEditor.PromptText);

        // 하단 예약 영역은 auto-wrap에 기대면(마지막 화면행 wrap이 스크롤 영역을 침범) 깨진다.
        // 프롬프트+버퍼를 폭 w 셀 단위로 직접 분할해 각 행을 절대 좌표로 그린다.
        var rows = SplitByCells(LineEditor.PromptText + buf.ToString(), w);
        var inputRows = rows.Count;
        // 레이아웃(위→아래): 입력행(배경색으로 구분) / 상태줄("act mode"). 구분선 없음.
        var reserved = inputRows + 1;
        var scrollBottom = h - reserved;              // 마지막 스크롤 행(1-기반)
        if (scrollBottom < 1)
        {
            scrollBottom = 1;
            reserved = h - 1;
        }

        var sb = new StringBuilder();
        if (!_installed)
        {
            // 신규 설치(입력 대기 시작): 하단에 reserved 줄 공간 확보 — 화면을 위로 스크롤해
            // 기존 출력은 스크롤백으로 보존하고, 그 빈 자리에 박스를 그린다(직전 출력을 덮지 않게).
            sb.Append($"\x1b[{h};1H");
            for (var k = 0; k < reserved; k++) sb.Append('\n');
            sb.Append($"\x1b[1;{scrollBottom}r");
            _reserved = reserved;
            _installed = true;
        }
        else if (reserved != _reserved)
        {
            // 편집 중 입력 줄 수 변화 → 스크롤 영역만 재설정.
            sb.Append($"\x1b[1;{scrollBottom}r");
            _reserved = reserved;
        }

        var inputRow0 = scrollBottom + 1;
        var statusRow = inputRow0 + inputRows;

        // 입력행: 각 행을 clear 후 절대 좌표로 직접 출력(auto-wrap 미사용). 어두운 회색 배경(구분선 대체).
        var bg = LineEditor.InputBg;
        for (var i = 0; i < inputRows; i++)
        {
            sb.Append($"\x1b[{inputRow0 + i};1H\x1b[2K");
            if (bg.Length > 0) sb.Append(bg);
            if (i == 0)
            {
                // 첫 행 앞 프롬프트("❯ ") 초록. rows[0]은 프롬프트로 시작하므로 그 뒤만 기본색.
                var rest = rows[0].Length >= LineEditor.PromptText.Length
                    ? rows[0][LineEditor.PromptText.Length..]
                    : "";
                sb.Append("\x1b[32m").Append(LineEditor.PromptText).Append("\x1b[39m").Append(rest);
            }
            else
            {
                sb.Append("\x1b[39m").Append(rows[i]);
            }
            if (bg.Length > 0) sb.Append("\x1b[K\x1b[0m"); else sb.Append("\x1b[0m");
        }

        // 상태줄(입력창 아래). 구분선 없음.
        sb.Append($"\x1b[{statusRow};1H\x1b[2K").Append(statusOverride ?? _status());

        // 커서를 편집 위치로(절대 좌표) + 커서 표시(입력 차례). 처리 중엔 숨겨져 있다가 여기서 다시 보임.
        var curOff = plen + LineEditor.DisplayWidth(buf.ToString(0, pos));
        var curRow = inputRow0 + (curOff / w);
        var curCol = (curOff % w) + 1;
        sb.Append($"\x1b[{curRow};{curCol}H").Append("\x1b[?25h");

        Console.Write(sb.ToString());
    }

    // 입력 확정: 스크롤 영역을 해제해 턴 동안 '일반 터미널'로 되돌린다(→ 마우스휠 네이티브 스크롤백 정상).
    // 하단 박스를 지우고 입력한 명령을 일반 흐름으로 echo. 하단 고정은 다음 ReadLine 의 Draw 가 다시 세운다.
    private void SubmitAndTeardown(string text)
    {
        var boxTop = Math.Max(1, Height() - _reserved + 1);   // 현재 박스(상단 라인)가 시작하는 행
        var sb = new StringBuilder();
        sb.Append("\x1b[r");                                   // 스크롤 영역 해제(전체 화면 정상)
        sb.Append($"\x1b[{boxTop};1H\x1b[J");                  // 박스 있던 자리부터 이하 전체 지움
        sb.Append("\x1b[?25h");                                // 커서 표시
        sb.Append("\x1b[0m\x1b[32m").Append(LineEditor.PromptText).Append("\x1b[0m")
          .Append(text).Append('\n');                         // 명령 echo(일반 흐름 — 이후 출력이 정상 스크롤)
        Console.Write(sb.ToString());
        _installed = false;
        _reserved = 0;
    }

    /// <summary>
    /// 하단 고정 입력 한 줄 읽기. 반환 규칙은 LineEditor.ReadLine 과 동일(null=EOF/quit).
    /// cycleMode: Shift+Tab 시 모드 토글 후 새 상태줄 문자열 반환.
    /// </summary>
    public string? ReadLine(
        IReadOnlyList<string> history,
        IReadOnlyList<string> slashCommands,
        Func<string>? cycleMode)
    {
        var buf = new StringBuilder();
        var pos = 0;
        var histIdx = history.Count;
        var savedCurrent = "";
        Draw(buf, pos);

        // 붙여넣기를 ESC[200~ … ESC[201~ 로 감싸 받는다 → 붙여넣은 개행이 Enter 로 오인되지 않는다.
        Console.Write(BracketedPaste.Enable);
        try
        {
        while (true)
        {
            var key = BracketedPaste.ReadKey();

            // 붙여넣기: 여러 줄이면 표식으로 접어 넣는다. 도크가 예약한 행수가 그대로 유지된다.
            if (BracketedPaste.TryReadPaste(key, out var pasted))
            {
                PasteStore.Insert(buf, ref pos, pasted);
                Draw(buf, pos);
                continue;
            }

            if (key.Key == ConsoleKey.Enter || key.KeyChar == '\r' || key.KeyChar == '\n')
            {
                if (buf.ToString().Trim().Length == 0)
                {
                    continue; // 빈 입력 무시
                }

                var text = buf.ToString();
                SubmitAndTeardown(text);
                return text;
            }

            switch (key.Key)
            {
                case ConsoleKey.Backspace:
                    if (pos > 0)
                    {
                        // 붙여넣기 표식은 한 글자씩이 아니라 통째로 지운다.
                        var n = PasteStore.PlaceholderLengthEndingAt(buf.ToString(), pos);
                        var del = n > 0 ? n : 1;
                        buf.Remove(pos - del, del); pos -= del; DrawCoalesced(buf, pos);
                    }
                    break;
                case ConsoleKey.Delete:
                    if (pos < buf.Length) { buf.Remove(pos, 1); DrawCoalesced(buf, pos); }
                    break;
                case ConsoleKey.LeftArrow:
                    if (pos > 0) { pos--; Draw(buf, pos); }
                    break;
                case ConsoleKey.RightArrow:
                    if (pos < buf.Length) { pos++; Draw(buf, pos); }
                    break;
                case ConsoleKey.Home:
                    pos = 0; Draw(buf, pos);
                    break;
                case ConsoleKey.End:
                    pos = buf.Length; Draw(buf, pos);
                    break;
                case ConsoleKey.UpArrow:
                    if (histIdx > 0)
                    {
                        if (histIdx == history.Count) savedCurrent = buf.ToString();
                        histIdx--;
                        SetBuffer(buf, ref pos, history[histIdx]);
                        Draw(buf, pos);
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (histIdx < history.Count)
                    {
                        histIdx++;
                        SetBuffer(buf, ref pos, histIdx == history.Count ? savedCurrent : history[histIdx]);
                        Draw(buf, pos);
                    }
                    break;
                case ConsoleKey.Tab:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        if (cycleMode is not null)
                        {
                            Draw(buf, pos, cycleMode());
                        }
                        break;
                    }
                    if (TryComplete(buf, ref pos, slashCommands))
                    {
                        Draw(buf, pos);
                    }
                    break;
                default:
                    if (key.KeyChar == '') // Ctrl+D
                    {
                        if (buf.Length == 0) { return null; }
                        break;
                    }
                    if (key.KeyChar == '') // Ctrl+U
                    {
                        buf.Clear(); pos = 0; Draw(buf, pos);
                        break;
                    }
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Insert(pos, key.KeyChar); pos++;
                        DrawCoalesced(buf, pos);
                    }
                    break;
            }
        }
        }
        finally
        {
            Console.Write(BracketedPaste.Disable);
        }
    }

    // 붙여넣기 등 큐가 차 있으면 큐가 빌 때만 다시 그린다(대량 입력 빠르게).
    private void DrawCoalesced(StringBuilder buf, int pos)
    {
        bool more;
        try { more = Console.KeyAvailable; }
        catch { more = false; }
        if (more) return;
        Draw(buf, pos);
    }

    // 문자열을 표시폭 w 셀 단위로 분할(넓은 문자를 경계에서 쪼개지 않음). 최소 1개 행 반환.
    private static List<string> SplitByCells(string s, int w)
    {
        if (w < 1) w = 1;
        var rows = new List<string>();
        var cur = new StringBuilder();
        var cw = 0;
        foreach (var ch in s)
        {
            var cwid = LineEditor.CharWidth(ch);
            if (cw + cwid > w)
            {
                rows.Add(cur.ToString());
                cur.Clear();
                cw = 0;
            }
            cur.Append(ch);
            cw += cwid;
        }
        rows.Add(cur.ToString());
        return rows;
    }

    // ── 유틸 (docked 전용, 목록 출력 없음) ─────────────────────────────────────
    private static void SetBuffer(StringBuilder buf, ref int pos, string text)
    {
        buf.Clear(); buf.Append(text); pos = buf.Length;
    }

    // 단일 후보/공통 접두사만 완성(하단 고정에선 후보 목록 출력은 생략 — 레이아웃 안정).
    private static bool TryComplete(StringBuilder buf, ref int pos, IReadOnlyList<string> slash)
    {
        var text = buf.ToString();
        if (!text.StartsWith('/') || text.Contains(' ')) return false;
        var token = text[1..];
        var matches = slash
            .Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (matches.Count == 0) return false;
        if (matches.Count == 1) { SetBuffer(buf, ref pos, "/" + matches[0] + " "); return true; }
        var common = LongestCommonPrefix(matches);
        if (common.Length > token.Length) { SetBuffer(buf, ref pos, "/" + common); return true; }
        return false;
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return "";
        var prefix = items[0];
        foreach (var s in items)
        {
            var n = 0;
            while (n < prefix.Length && n < s.Length
                   && char.ToLowerInvariant(prefix[n]) == char.ToLowerInvariant(s[n])) n++;
            prefix = prefix[..n];
            if (prefix.Length == 0) break;
        }
        return prefix;
    }
}
