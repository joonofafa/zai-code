using System.Text;
using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

// Console.Out 리다이렉트 + LineEditor.ColsForTest(전역)을 쓰므로 직렬화한다.
[Collection("EnvMutating")]
public class LineEditorWrapTests
{
    // ── 최소 가상 터미널 (와이드 문자 no-straddle · deferred-wrap · 바닥 스크롤) ──
    private sealed class VTerm
    {
        private readonly int _cols;
        private readonly int _rows;
        private readonly char[][] _grid;
        private int _cx;            // 커서 열(0-based)
        private int _cy;            // 커서 행(0-based)
        private bool _pending;      // 마지막 열을 채워 wrap 이 지연된 상태

        public VTerm(int cols, int rows)
        {
            _cols = cols;
            _rows = rows;
            _grid = new char[rows][];
            for (var r = 0; r < rows; r++)
            {
                _grid[r] = new char[cols];
                Array.Fill(_grid[r], ' ');
            }
        }

        public void Feed(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    i += 2;
                    var num = new StringBuilder();
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == ';'))
                    {
                        num.Append(s[i]);
                        i++;
                    }

                    if (i >= s.Length) break;
                    var final = s[i];
                    var p = num.Length > 0 && int.TryParse(num.ToString().Split(';')[0], out var v) ? v : -1;
                    ApplyCsi(final, p);
                    continue;
                }

                if (ch == '\r') { _cx = 0; _pending = false; continue; }
                if (ch == '\n') { _cy++; ScrollIfNeeded(); _pending = false; continue; }
                Put(ch);
            }
        }

        private void ApplyCsi(char final, int p)
        {
            switch (final)
            {
                case 'A': _cy = Math.Max(0, _cy - (p < 0 ? 1 : p)); _pending = false; break;
                case 'B': _cy = Math.Min(_rows - 1, _cy + (p < 0 ? 1 : p)); _pending = false; break;
                case 'C': _cx = Math.Min(_cols - 1, _cx + (p < 0 ? 1 : p)); _pending = false; break;
                case 'D': _cx = Math.Max(0, _cx - (p < 0 ? 1 : p)); _pending = false; break;
                case 'K':
                    // 커서~행끝 지움. deferred(마지막 열 채움) 상태면 지울 것 없음.
                    if (!_pending)
                    {
                        for (var c = _cx; c < _cols; c++) _grid[_cy][c] = ' ';
                    }
                    break;
                // 'm'(SGR) 등은 커서/내용에 영향 없음 → 무시.
            }
        }

        private void Put(char ch)
        {
            if (_pending) { _cx = 0; _cy++; ScrollIfNeeded(); _pending = false; }

            var w = LineEditor.CharWidth(ch);
            if (w == 2 && _cx == _cols - 1)
            {
                _grid[_cy][_cx] = ' '; // 와이드 문자는 마지막 한 칸에 안 들어감 → 빈칸 남기고 다음 행
                _cx = 0;
                _cy++;
                ScrollIfNeeded();
            }

            _grid[_cy][_cx] = ch;
            if (w == 2 && _cx + 1 < _cols) _grid[_cy][_cx + 1] = '\0'; // 와이드 문자 둘째 셀 표식

            var nx = _cx + w;
            if (nx >= _cols) { _cx = _cols - 1; _pending = true; }
            else { _cx = nx; }
        }

        private void ScrollIfNeeded()
        {
            while (_cy >= _rows)
            {
                for (var r = 1; r < _rows; r++) _grid[r - 1] = _grid[r];
                _grid[_rows - 1] = new char[_cols];
                Array.Fill(_grid[_rows - 1], ' ');
                _cy--;
            }
        }

        public int RowsContaining(char c)
        {
            var n = 0;
            for (var r = 0; r < _rows; r++)
            {
                if (Array.IndexOf(_grid[r], c) >= 0) n++;
            }

            return n;
        }

        public string VisibleText()
        {
            var sb = new StringBuilder();
            for (var r = 0; r < _rows; r++)
            {
                foreach (var c in _grid[r])
                {
                    if (c != ' ' && c != '\0') sb.Append(c);
                }
            }

            return sb.ToString();
        }

        // 커서를 맨 아래 행으로(프롬프트가 화면 바닥에 있는 상황 재현).
        public void MoveToBottom()
        {
            for (var i = 0; i < _rows - 1; i++) Feed("\n");
        }
    }

    private static void Step(VTerm vt, LineEditor.PromptRenderer r, StringBuilder buf)
    {
        var prev = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { r.Refresh(buf, buf.Length); }
        finally { Console.SetOut(prev); }
        vt.Feed(sw.ToString());
    }

    [Fact]
    public void Wide_char_grow_then_shrink_does_not_duplicate_prompt_line()
    {
        var prevCols = LineEditor.ColsForTest;
        var prevBg = Environment.GetEnvironmentVariable("MOAI_PROMPT_BG");
        try
        {
            LineEditor.ColsForTest = 21;                 // 홀수 폭 → 매 행 col20 을 못 채워 straddle 누적
            const string hangul =
                "동해물과백두산이마르고닳도록하느님이보우하사우리나라만세무궁화삼천리화려강산대한사람";

            var vt = new VTerm(21, 8);
            vt.MoveToBottom();                           // 프롬프트가 화면 바닥 → grow 시 스크롤 발생(Bill 재현 조건)
            var r = new LineEditor.PromptRenderer(false, Array.Empty<string>(), false);
            var buf = new StringBuilder();

            // 타이핑(grow): 2행 이상으로 접힐 때까지 한 글자씩.
            foreach (var ch in hangul)
            {
                buf.Append(ch);
                Step(vt, r, buf);
            }

            // 프롬프트 '❯' 는 grow 직후에도 화면에 딱 한 번(중복 없음).
            Assert.Equal(1, vt.RowsContaining('❯'));

            // 백스페이스(shrink): 다시 1행이 될 때까지 한 글자씩 지움.
            while (buf.Length > 3)
            {
                buf.Remove(buf.Length - 1, 1);
                Step(vt, r, buf);
            }

            // 프롬프트 '❯' 는 화면에 딱 한 번만 있어야 한다(첫 줄 중복 없음).
            Assert.Equal(1, vt.RowsContaining('❯'));
            // 남은 버퍼 내용도 정확히 한 번만(공백은 양쪽 동일 처리).
            Assert.Equal(("❯ " + buf).Replace(" ", ""), vt.VisibleText());
        }
        finally
        {
            LineEditor.ColsForTest = prevCols;
            Environment.SetEnvironmentVariable("MOAI_PROMPT_BG", prevBg);
        }
    }
}
