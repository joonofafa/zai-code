using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// raw 모드 한 줄 입력기. Console.ReadLine 대신 키 단위로 읽어 명령 히스토리(↑/↓),
/// 슬래시 자동완성(Tab), 커서 이동/편집(←/→/Home/End/Backspace)을 지원한다.
/// 비대화형(파이프/리다이렉트)에서는 Console.ReadLine 으로 폴백.
///
/// wrap 처리: 입력이 터미널 폭을 넘어 여러 물리 행으로 접히면(wrap) 재그리기가 깨지지 않도록
/// linenoise 의 멀티라인 refresh 알고리즘을 사용한다 — 직전 렌더가 차지한 물리 행 수(oldRows)와
/// 커서 행(oldPos)을 추적해, 매번 블록 전체를 지우고 다시 그린 뒤 커서를 정확한 행/열로 옮긴다.
/// 폭을 정확히 채운(exact-fill) 경우의 deferred-wrap 모호성은 줄바꿈 강제로 보정한다.
/// 붙여넣기: Console.KeyAvailable 로 입력 큐가 빌 때만 한 번 refresh 하여 대량 입력을 빠르게 처리.
/// </summary>
public static class LineEditor
{
    internal const string PromptText = "❯ ";
    public const string CycleModeSignal = "__cycle_mode__";

    // 사용자 프롬프트(입력) 라인 배경 — 약간 어두운 회색(256색 236 ≈ #303030). 환경변수
    // MOAI_PROMPT_BG 로 256색 인덱스(0~255)를 지정해 조정, "off" 면 배경 없음. escape 코드는
    // 표시폭 0 이라 wrap 계산에 영향 없음. (BottomDock 도 공유)
    internal static readonly string InputBg = ResolveInputBg();

    private static string ResolveInputBg()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_PROMPT_BG");
        if (string.Equals(env, "off", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        if (int.TryParse(env, out var n) && n is >= 0 and <= 255)
        {
            return $"\x1b[48;5;{n}m";
        }
        return "\x1b[48;5;236m";
    }

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

        // 상태줄이 있으면 프롬프트 위에 한 줄 출력 후 시작. 이후 refresh 는 프롬프트 블록(아래쪽)만
        // 다시 그리므로 위의 상태줄은 보존된다 (모드 토글 시에만 명시적으로 다시 그림).
        var hasStatus = statusLine is not null;
        if (hasStatus)
        {
            Console.WriteLine(statusLine!());
        }

        var buf = new StringBuilder();
        var pos = 0;
        var histIdx = history.Count;
        var savedCurrent = "";
        var r = new PromptRenderer(hasStatus);
        r.Refresh(buf, pos);

        // 붙여넣기를 ESC[200~ … ESC[201~ 로 감싸 받는다 → 붙여넣은 개행이 Enter 로 오인되지 않는다.
        Console.Write(BracketedPaste.Enable);
        try
        {
        while (true)
        {
            var key = BracketedPaste.ReadKey();

            // 붙여넣기: 여러 줄이면 표식으로 접어 넣는다(전송하지 않음).
            if (BracketedPaste.TryReadPaste(key, out var pasted))
            {
                PasteStore.Insert(buf, ref pos, pasted);
                r.Refresh(buf, pos);
                continue;
            }

            if (key.Key == ConsoleKey.Enter || key.KeyChar == '\r' || key.KeyChar == '\n')
            {
                // 아무것도(공백만) 입력하지 않은 Enter 는 무시 — 새 프롬프트 라인으로 넘어가지 않고
                // 같은 자리를 유지한다.
                if (buf.ToString().Trim().Length == 0)
                {
                    continue;
                }

                r.Finish();
                return buf.ToString();
            }

            switch (key.Key)
            {
                case ConsoleKey.Backspace:
                    if (pos > 0)
                    {
                        // 붙여넣기 표식은 한 글자씩이 아니라 통째로 지운다.
                        var n = PasteStore.PlaceholderLengthEndingAt(buf.ToString(), pos);
                        var del = n > 0 ? n : 1;
                        buf.Remove(pos - del, del); pos -= del; DrawCoalesced(r, buf, pos);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (pos < buf.Length) { buf.Remove(pos, 1); DrawCoalesced(r, buf, pos); }
                    break;

                case ConsoleKey.LeftArrow:
                    if (pos > 0) { pos--; r.Refresh(buf, pos); }
                    break;

                case ConsoleKey.RightArrow:
                    if (pos < buf.Length) { pos++; r.Refresh(buf, pos); }
                    break;

                case ConsoleKey.Home:
                    pos = 0; r.Refresh(buf, pos);
                    break;

                case ConsoleKey.End:
                    pos = buf.Length; r.Refresh(buf, pos);
                    break;

                case ConsoleKey.UpArrow:
                    if (histIdx > 0)
                    {
                        if (histIdx == history.Count) savedCurrent = buf.ToString();
                        histIdx--;
                        SetBuffer(buf, ref pos, history[histIdx]);
                        r.Refresh(buf, pos);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (histIdx < history.Count)
                    {
                        histIdx++;
                        SetBuffer(buf, ref pos,
                            histIdx == history.Count ? savedCurrent : history[histIdx]);
                        r.Refresh(buf, pos);
                    }
                    break;

                case ConsoleKey.Tab:
                    if (key.Modifiers.HasFlag(ConsoleModifiers.Shift))
                    {
                        if (cycleMode is not null)
                        {
                            // 모드 토글: 상태줄 갱신 후 프롬프트와 함께 다시 그린다.
                            r.Refresh(buf, pos, cycleMode());
                            break;
                        }
                        r.Finish();
                        return CycleModeSignal;
                    }
                    {
                        var (changed, listed) = TryComplete(buf, ref pos, slashCommands);
                        if (listed)
                        {
                            // 후보 목록을 새 줄에 출력했으므로 프롬프트를 새 줄에 새로 그린다.
                            r.ResetFresh();
                            r.Refresh(buf, pos);
                        }
                        else if (changed)
                        {
                            r.Refresh(buf, pos);
                        }
                    }
                    break;

                default:
                    if (key.KeyChar == '') // Ctrl+D
                    {
                        if (buf.Length == 0) { r.Finish(); return null; }
                        break;
                    }
                    if (key.KeyChar == '') // Ctrl+U
                    {
                        buf.Clear(); pos = 0;
                        r.Refresh(buf, pos);
                        break;
                    }
                    if (!char.IsControl(key.KeyChar))
                    {
                        buf.Insert(pos, key.KeyChar);
                        pos++;
                        DrawCoalesced(r, buf, pos);
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

    // 붙여넣기 등으로 입력 큐가 차 있으면 매 키마다 다시 그리지 않고 큐가 빌 때 한 번만 그린다.
    // (마지막 refresh 가 wrap 을 정확히 처리하므로 대량 입력도 올바르게 표시된다.)
    private static void DrawCoalesced(PromptRenderer r, StringBuilder buf, int pos)
    {
        bool more;
        try { more = Console.KeyAvailable; }
        catch { more = false; }

        if (more) return;
        r.Refresh(buf, pos);
    }

    // ── wrap 레이아웃 계산 (순수 함수 — 단위 테스트 대상) ────────────────────────

    /// <summary>표시폭 <paramref name="total"/> 셀이 폭 <paramref name="cols"/> 터미널에서 차지하는 물리 행 수(최소 1).</summary>
    public static int RowCount(int total, int cols)
    {
        if (cols < 1) cols = 1;
        return Math.Max(1, (total + cols - 1) / cols);
    }

    /// <summary>프롬프트 시작에서 표시폭 <paramref name="offset"/> 떨어진 지점의 1-기반 물리 행 번호.</summary>
    public static int RowOf(int offset, int cols)
    {
        if (cols < 1) cols = 1;
        return (offset + cols) / cols;
    }

    /// <summary>프롬프트 시작에서 표시폭 <paramref name="offset"/> 떨어진 지점의 0-기반 열 번호.</summary>
    public static int ColOf(int offset, int cols)
    {
        if (cols < 1) cols = 1;
        return offset % cols;
    }

    // ── 렌더러 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 프롬프트 블록(프롬프트 + 버퍼)을 wrap-정확하게 다시 그린다. 직전 렌더가 차지한 물리 행 수와
    /// 커서 행을 추적해 잔상 없이 갱신한다. linenoise refreshMultiLine 이식.
    /// </summary>
    private sealed class PromptRenderer
    {
        private readonly bool _hasStatus;
        private int _oldRows = 1;   // 직전 렌더가 차지한 물리 행 수(>=1)
        private int _oldOff;        // 직전 렌더에서 커서가 있던 표시폭 오프셋(프롬프트 시작 기준 버퍼 내)

        public PromptRenderer(bool hasStatus) => _hasStatus = hasStatus;

        private static int Cols()
        {
            int w;
            try { w = Console.WindowWidth; }
            catch { w = 80; }
            return w < 1 ? 80 : w;
        }

        /// <summary>다음 Refresh 가 빈 줄에서 새로 시작한다고 가정(자동완성 목록 출력 후 등).</summary>
        public void ResetFresh()
        {
            _oldRows = 1;
            _oldOff = 0;
        }

        public void Refresh(StringBuilder buf, int pos, string? newStatus = null)
        {
            var cols = Cols();
            var plen = DisplayWidth(PromptText);
            var blen = DisplayWidth(buf.ToString());
            var curOff = plen + DisplayWidth(buf.ToString(0, pos)); // 커서까지의 표시폭(프롬프트 포함)
            var total = plen + blen;

            var rows = RowCount(total, cols);
            var oldRpos = RowOf(plen + _oldOff, cols); // 직전 커서의 1-기반 행
            var sb = new StringBuilder();

            // 1) 직전 블록의 마지막 행으로 내려간다.
            if (_oldRows - oldRpos > 0)
            {
                sb.Append($"\x1b[{_oldRows - oldRpos}B");
            }

            // 2) 마지막 행부터 위로 올라가며 각 행을 지운다(프롬프트 첫 행 바로 위까지).
            for (var j = 0; j < _oldRows - 1; j++)
            {
                sb.Append("\r\x1b[0K\x1b[1A");
            }

            // 3) 프롬프트 첫 행을 지운다.
            sb.Append("\r\x1b[0K");

            // 3b) 상태줄 갱신(프롬프트 한 행 위). 모드 토글 시에만.
            if (newStatus is not null && _hasStatus)
            {
                sb.Append("\x1b[1A\r\x1b[0K").Append(newStatus).Append('\n');
            }

            // 4) 프롬프트 + 버퍼 출력. 입력 라인 배경을 어두운 회색으로 깔고(InputBg),
            //    \x1b[K 로 마지막 행의 남은 폭까지 같은 배경색으로 채워 입력 필드처럼 보이게 한다.
            //    (배경 없이 쓰려면 MOAI_PROMPT_BG=off)
            if (InputBg.Length > 0)
            {
                sb.Append(InputBg)                         // 배경 on
                  .Append("\x1b[32m").Append(PromptText)   // 초록 화살표
                  .Append("\x1b[39m").Append(buf.ToString()) // 전경 기본(배경 유지) + 버퍼
                  .Append("\x1b[K")                         // 마지막 행 남은 폭을 배경색으로 채움
                  .Append("\x1b[0m");                       // 리셋
            }
            else
            {
                sb.Append("\x1b[32m").Append(PromptText).Append("\x1b[0m").Append(buf.ToString());
            }

            // 5) exact-fill 보정: 커서가 끝이고 끝이 폭을 정확히 채워 다음 행 0열로 넘어가는 경우,
            //    deferred-wrap 모호성을 없애기 위해 줄바꿈을 강제한다.
            if (pos == buf.Length && buf.Length > 0 && total % cols == 0)
            {
                sb.Append("\r\n");
                rows++;
            }

            // 6) 커서를 목표 행으로 올린다.
            var curRpos = RowOf(curOff, cols);
            if (rows - curRpos > 0)
            {
                sb.Append($"\x1b[{rows - curRpos}A");
            }

            // 7) 목표 열로 이동.
            var col = ColOf(curOff, cols);
            sb.Append('\r');
            if (col > 0)
            {
                sb.Append($"\x1b[{col}C");
            }

            Console.Write(sb.ToString());
            _oldRows = rows;
            _oldOff = curOff - plen; // 버퍼 내 오프셋으로 보관(다음 oldRpos 계산은 plen 다시 더함)
        }

        /// <summary>입력 종료: 커서를 블록 마지막 행 아래로 옮겨 이후 출력이 프롬프트 밑에 오게 한다.</summary>
        public void Finish()
        {
            var cols = Cols();
            var plen = DisplayWidth(PromptText);
            var oldRpos = RowOf(plen + _oldOff, cols);
            var sb = new StringBuilder();
            if (_oldRows - oldRpos > 0)
            {
                sb.Append($"\x1b[{_oldRows - oldRpos}B");
            }
            sb.Append("\r\n");
            Console.Write(sb.ToString());
        }
    }

    // ── 유틸 ──────────────────────────────────────────────────────────────────

    private static void SetBuffer(StringBuilder buf, ref int pos, string text)
    {
        buf.Clear(); buf.Append(text); pos = buf.Length;
    }

    // 자동완성 시도. 반환: changed=버퍼 변경됨, listed=후보 목록을 새 줄에 출력함.
    private static (bool Changed, bool Listed) TryComplete(
        StringBuilder buf, ref int pos, IReadOnlyList<string> slashCommands)
    {
        var text = buf.ToString();
        if (!text.StartsWith('/') || text.Contains(' ')) return (false, false);
        var token = text[1..];
        var matches = slashCommands
            .Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.Ordinal).ToList();
        if (matches.Count == 0) return (false, false);
        if (matches.Count == 1) { SetBuffer(buf, ref pos, "/" + matches[0] + " "); return (true, false); }
        var common = LongestCommonPrefix(matches);
        if (common.Length > token.Length) { SetBuffer(buf, ref pos, "/" + common); return (true, false); }
        Console.WriteLine();
        Console.WriteLine("\x1b[38;5;250m  " +
            string.Join("   ", matches.Select(m => "/" + m)) + "\x1b[0m");
        return (false, true);
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

    internal static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var c in s) w += IsWide(c) ? 2 : 1;
        return w;
    }

    /// <summary>한 문자의 표시폭(1 또는 2). 하단 고정 입력의 행 분할에 사용.</summary>
    internal static int CharWidth(char c) => IsWide(c) ? 2 : 1;

    private static bool IsWide(char c) =>
        (c >= 'ᄀ' && c <= 'ᅟ') ||
        (c >= '⺀' && c <= '꓏') ||
        (c >= '가' && c <= '힣') ||
        (c >= '豈' && c <= '﫿') ||
        (c >= '︰' && c <= '﹏') ||
        (c >= '＀' && c <= '｠') ||
        (c >= '￠' && c <= '￦');
}
