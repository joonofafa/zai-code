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

    // 인라인 자동완성(ghost) 색 — 연한 회색(256색 244). 입력 배경 위에서 흐릿하게 보인다.
    internal const string GhostColor = "\x1b[38;5;244m";

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

    // 셸 모드('!' 접두) 입력 라인 배경 — 어두운 빨강(256색 52 ≈ #5f0000). 이 모드에선 '❯' 프롬프트를
    // 숨긴다(폭 유지를 위해 공백 2칸으로 대체 — wrap/커서 계산 불변). MOAI_SHELL_BG 로 256색 조정 가능.
    internal static readonly string ShellBg = ResolveShellBg();

    private static string ResolveShellBg()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_SHELL_BG");
        if (int.TryParse(env, out var n) && n is >= 0 and <= 255)
        {
            return $"\x1b[48;5;{n}m";
        }
        return "\x1b[48;5;52m";
    }

    // 브레인스토밍 모드 입력 라인 배경 — 어두운 파랑(256색 18 ≈ #000087). (BottomDock 은 펄스 애니메이션,
    // 이 폴백 에디터는 정적 파랑.) MOAI_BRAINSTORM_BG 로 256색 조정 가능.
    internal static readonly string BrainstormBg = ResolveBrainstormBg();

    private static string ResolveBrainstormBg()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_BRAINSTORM_BG");
        if (int.TryParse(env, out var n) && n is >= 0 and <= 255)
        {
            return $"\x1b[48;5;{n}m";
        }
        return "\x1b[48;5;18m";
    }

    public static string? ReadLine(
        IReadOnlyList<string> history,
        IReadOnlyList<string> slashCommands,
        Func<string>? cycleMode = null,
        Func<string>? statusLine = null,
        bool brainstorm = false)
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
        var r = new PromptRenderer(hasStatus, slashCommands, brainstorm);
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
                    if (TryComplete(buf, ref pos, slashCommands))
                    {
                        r.Refresh(buf, pos);
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
        private readonly IReadOnlyList<string> _slash;
        private readonly bool _brainstorm;   // 브레인스토밍 모드: 입력 라인 배경 파랑
        private int _oldRows = 1;   // 직전 렌더가 차지한 물리 행 수(>=1)
        private int _oldOff;        // 직전 렌더에서 커서가 있던 표시폭 오프셋(프롬프트 시작 기준 버퍼 내)

        public PromptRenderer(bool hasStatus, IReadOnlyList<string> slash, bool brainstorm)
        {
            _hasStatus = hasStatus;
            _slash = slash;
            _brainstorm = brainstorm;
        }

        private static int Cols()
        {
            int w;
            try { w = Console.WindowWidth; }
            catch { w = 80; }
            return w < 1 ? 80 : w;
        }

        public void Refresh(StringBuilder buf, int pos, string? newStatus = null)
        {
            var cols = Cols();
            var plen = DisplayWidth(PromptText);
            var blen = DisplayWidth(buf.ToString());
            var curOff = plen + DisplayWidth(buf.ToString(0, pos)); // 커서까지의 표시폭(프롬프트 포함)
            var total = plen + blen;

            // 인라인 자동완성(ghost): 커서가 버퍼 끝이고 프롬프트+버퍼+ghost 가 한 줄에 들어갈 때만
            // 버퍼 뒤에 연한 글자로 덧그린다(줄바꿈/커서 계산은 버퍼 기준 그대로 — 리스크 격리).
            var ghost = GhostSuffix(buf.ToString(), _slash);
            var showGhost = ghost.Length > 0 && pos == buf.Length && total + DisplayWidth(ghost) <= cols;

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
            var shell = buf.Length > 0 && buf[0] == '!';   // '!' 셸 모드: 어두운 빨강 배경 + '❯' 숨김
            if (InputBg.Length > 0)
            {
                sb.Append(shell ? ShellBg : _brainstorm ? BrainstormBg : InputBg); // 배경 on (셸=빨강, 브레인스토밍=파랑)
                if (shell)
                {
                    sb.Append("  ");                       // '❯' 제거 — 폭 유지 위해 공백 2칸(wrap 계산 불변)
                }
                else
                {
                    sb.Append("\x1b[32m").Append(PromptText).Append("\x1b[39m"); // 초록 화살표
                }

                sb.Append(buf.ToString());                 // 전경 기본(배경 유지) + 버퍼
                if (showGhost) sb.Append(GhostColor).Append(ghost).Append("\x1b[39m"); // ghost(연한 글자)
                sb.Append("\x1b[K")                         // 마지막 행 남은 폭을 배경색으로 채움
                  .Append("\x1b[0m");                       // 리셋
            }
            else
            {
                if (shell)
                {
                    sb.Append("  ").Append(buf.ToString());
                }
                else
                {
                    sb.Append("\x1b[32m").Append(PromptText).Append("\x1b[0m").Append(buf.ToString());
                }

                if (showGhost) sb.Append(GhostColor).Append(ghost).Append("\x1b[0m"); // ghost(연한 글자)
            }

            // 5) exact-fill 보정: 커서가 끝이고 끝이 폭을 정확히 채워 다음 행 0열로 넘어가는 경우,
            //    deferred-wrap 모호성을 없앤다. ghost 표시 중엔 커서가 그린 끝이 아니므로 건너뛴다.
            if (!showGhost && pos == buf.Length && buf.Length > 0 && total % cols == 0)
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

    // Tab 자동완성: 현재 보이는 ghost(= 첫 매치)를 그대로 확정한다. 버퍼가 바뀌면 true.
    private static bool TryComplete(StringBuilder buf, ref int pos, IReadOnlyList<string> slashCommands)
        => AcceptCompletion(buf, ref pos, slashCommands);

    /// <summary>버퍼가 "/토큰"(공백 없음)일 때 알파벳순 첫 매치 명령. 없으면 null.</summary>
    public static string? FirstMatch(string text, IReadOnlyList<string> slash)
    {
        if (!text.StartsWith('/') || text.Contains(' ')) return null;
        var token = text[1..];
        return slash
            .Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// 인라인 ghost 로 흐릿하게 보여줄 접미(첫 매치의 아직 안 친 나머지 글자). 최소 "/x" 부터,
    /// 매치가 없거나 이미 완전히 친 경우 빈 문자열. 실제 화면 표시 여부(한 줄에 들어가는지)는 렌더러가 판단.
    /// </summary>
    public static string GhostSuffix(string text, IReadOnlyList<string> slash)
    {
        // '/' 명령 ghost (전체 라인 "/토큰") — 최소 한 글자("/x")는 쳐야 ghost. 바 '/' 단독은 표시 안 함.
        if (text.Length >= 2)
        {
            var best = FirstMatch(text, slash);
            if (best is not null)
            {
                var token = text[1..];
                return best.Length > token.Length ? best[token.Length..] : string.Empty;
            }
        }

        // '@' 파일 멘션 ghost (현재 디렉토리 기준 파일명 자동완성) — "@" 단독(길이 1)도 첫 파일 표시.
        return FileMentionGhost(text);
    }

    /// <summary>Tab 확정: '/' 명령(전체 라인 치환) 또는 '@' 파일 멘션(현재 토큰에 완성 부착). 바뀌면 true.</summary>
    internal static bool AcceptCompletion(StringBuilder buf, ref int pos, IReadOnlyList<string> slash)
    {
        var text = buf.ToString();
        var best = FirstMatch(text, slash);
        if (best is not null)
        {
            buf.Clear();
            buf.Append('/').Append(best).Append(' ');
            pos = buf.Length;
            return true;
        }

        if (pos == buf.Length)
        {
            var g = FileMentionGhost(text);
            if (g.Length > 0)
            {
                buf.Append(g);
                pos = buf.Length;
                return true;
            }
        }

        return false;
    }

    /// <summary>텍스트 끝 토큰이 '@경로'면, 경로의 디렉토리(없으면 cwd) 기준 첫 매치 파일의 나머지 글자를 ghost로.</summary>
    internal static string FileMentionGhost(string text)
    {
        var tokenStart = text.LastIndexOfAny(new[] { ' ', '\t' }) + 1;
        var token = text[tokenStart..];
        if (token.Length < 1 || token[0] != '@')
        {
            return string.Empty;
        }

        var partial = token[1..]; // '@' 뒤 경로(빈 문자열 가능 → 첫 파일)
        var match = FirstFileMatch(partial);
        if (match is null)
        {
            return string.Empty;
        }

        var nameStart = partial.LastIndexOfAny(new[] { '/', '\\' }) + 1;
        var typedName = partial[nameStart..];
        var suffix = match.Value.Name.Length > typedName.Length ? match.Value.Name[typedName.Length..] : string.Empty;
        return match.Value.IsDir ? suffix + "/" : suffix;
    }

    // '@' 뒤 부분경로에 대한 첫 매치 파일/디렉토리 (이름, 디렉토리 여부). 없으면 null.
    private static (string Name, bool IsDir)? FirstFileMatch(string partial)
    {
        var slashIdx = partial.LastIndexOfAny(new[] { '/', '\\' });
        var dirPart = slashIdx >= 0 ? partial[..(slashIdx + 1)] : string.Empty;
        var prefix = slashIdx >= 0 ? partial[(slashIdx + 1)..] : partial;

        string searchDir;
        try
        {
            searchDir = Path.GetFullPath(dirPart.Length == 0 ? "." : dirPart, Directory.GetCurrentDirectory());
        }
        catch
        {
            return null;
        }

        if (!Directory.Exists(searchDir))
        {
            return null;
        }

        string? match;
        try
        {
            match = Directory.EnumerateFileSystemEntries(searchDir)
                .Select(p => Path.GetFileName(p) ?? string.Empty)
                .Where(n => n.Length > 0
                            && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (prefix.StartsWith('.') || !n.StartsWith('.'))) // prefix가 '.' 아니면 숨김 제외
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }

        if (match is null)
        {
            return null;
        }

        bool isDir;
        try
        {
            isDir = Directory.Exists(Path.Combine(searchDir, match));
        }
        catch
        {
            isDir = false;
        }

        return (match, isDir);
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
