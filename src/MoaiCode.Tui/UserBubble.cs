using System.Text;

namespace MoaiCode.Tui;

/// <summary>
/// 사용자가 입력한 명령을 채팅처럼 우측 정렬 버블(둥근 테두리)로 echo 한다.
/// 모델 응답 패널(전체 폭 좌측)과 대비돼 누가 말한 줄인지 한눈에 보이게 한다.
/// 스크롤 영역/일반 터미널 양쪽에서 쓰므로 순수 텍스트(개행 포함)만 반환하는 팩토리 방식.
/// </summary>
public static class UserBubble
{
    // 버블 최대 폭(터미널 폭 대비). 긴 입력은 버블 안에서 여러 줄로 wrap 된다.
    private const double MaxRatio = 0.7;
    private const int MaxCols = 72;
    private const int MinCols = 16;

    /// <summary>우측 정렬 버블 원문(텍스트만, ANSI 색 포함). 셸 모드는 회색 테두리.</summary>
    public static string Render(string text, int terminalCols, bool shell = false)
    {
        var w = Math.Max(MinCols, Math.Min(MaxCols, (int)(terminalCols * MaxRatio)));
        var edge = shell ? '╶' : '╮';
        // 내용을 표시폭 기준 w-6 셀로 wrap. 행 구성: '│'+여백2+내용+여백2+'│' = w (테두리 2 + 안쪽여백 4).
        var inner = w - 6;
        var lines = WrapByCells(text, inner);
        // 마지막 열에 정확히 닿으면 auto-wrap 로 줄이 하나 더 생긴다 — 여유 1칸을 남긴다.
        var pad = Math.Max(1, terminalCols - w - 1);

        var dim = shell ? "\x1b[38;5;244m" : "\x1b[38;5;71m";   // 테두리: 셸=회색, 일반=연두
        var body = shell ? "\x1b[38;5;250m" : "\x1b[38;5;254m";
        var sb = new StringBuilder();
        sb.Append(' ', pad).Append(dim).Append('╭').Append('─', w - 2).Append(edge).Append("\x1b[0m\n");
        foreach (var line in lines)
        {
            var lw = LineEditor.DisplayWidth(line);
            // 행 폭: '│' + 안쪽여백2 + 내용(inner) + 여백2 + '│' = w. 여백이 남으면 오른쪽으로 채운다.
            sb.Append(' ', pad).Append(dim).Append('│').Append("\x1b[0m").Append(body)
              .Append("  ").Append(line).Append(' ', Math.Max(0, inner - lw))
              .Append("  \x1b[0m").Append(dim).Append('│').Append("\x1b[0m\n");
        }

        sb.Append(' ', pad).Append(dim).Append(shell ? '╶' : '╰').Append('─', w - 2)
          .Append(shell ? '╶' : '╯').Append("\x1b[0m");
        return sb.ToString();
    }

    // 표시폭(wide=2셀) 기준 줄바꿈. 단어 경계 우선, 못 하면 셀 단위.
    private static List<string> WrapByCells(string s, int w)
    {
        var result = new List<string>();
        if (w < 1)
        {
            w = 1;
        }

        foreach (var rawLine in s.Replace("\r", "").Split('\n'))
        {
            var cur = new StringBuilder();
            var cw = 0;
            foreach (var ch in rawLine)
            {
                var chw = LineEditor.CharWidth(ch);
                if (cw + chw > w)
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                    cw = 0;
                }

                cur.Append(ch);
                cw += chw;
            }

            result.Add(cur.ToString());
        }

        return result;
    }
}
