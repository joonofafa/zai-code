using System.Text;

namespace MoaiCode.Tools.OpenXml;

/// <summary>추출된 평문을 겹치는(overlap) 청크로 나눈다. 크기 상한을 지키되 단어 중간을 피해 공백에서 끊는다.</summary>
public static class TextChunker
{
    public const int DefaultMaxChars = 1000;
    public const int DefaultOverlapChars = 150;

    public static IReadOnlyList<string> Chunk(
        string text, int maxChars = DefaultMaxChars, int overlapChars = DefaultOverlapChars)
    {
        if (maxChars < 100)
        {
            maxChars = 100;
        }

        overlapChars = Math.Clamp(overlapChars, 0, maxChars / 2);

        // 개행 정규화 + 과도한 빈 줄 압축.
        var normalized = NormalizeWhitespace(text);
        if (normalized.Length == 0)
        {
            return Array.Empty<string>();
        }

        var chunks = new List<string>();
        var pos = 0;
        while (pos < normalized.Length)
        {
            var end = Math.Min(pos + maxChars, normalized.Length);

            // 하드 리밋 전이면 마지막 공백/개행에서 끊어 단어 절단 방지(청크의 뒤 절반 범위에서 탐색).
            if (end < normalized.Length)
            {
                var snap = LastBreakBefore(normalized, end, pos + (maxChars / 2));
                if (snap > pos)
                {
                    end = snap;
                }
            }

            var piece = normalized[pos..end].Trim();
            if (piece.Length > 0)
            {
                chunks.Add(piece);
            }

            if (end >= normalized.Length)
            {
                break;
            }

            // 다음 시작점: overlap 만큼 뒤로. 항상 전진 보장.
            pos = Math.Max(end - overlapChars, pos + 1);
        }

        return chunks;
    }

    private static int LastBreakBefore(string s, int end, int min)
    {
        for (var i = end - 1; i >= min && i > 0; i--)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NormalizeWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var newlineRun = 0;
        foreach (var ch in text.ReplaceLineEndings("\n"))
        {
            if (ch == '\n')
            {
                newlineRun++;
                if (newlineRun <= 2)
                {
                    sb.Append('\n'); // 문단 구분(최대 2개)까지만 유지
                }
            }
            else
            {
                newlineRun = 0;
                sb.Append(ch);
            }
        }

        return sb.ToString().Trim();
    }
}
