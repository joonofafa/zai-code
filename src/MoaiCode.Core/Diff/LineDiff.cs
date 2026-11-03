namespace MoaiCode.Core.Diff;

/// <summary>
/// 통합 diff(unified diff) 의 한 덩어리. <see cref="Lines"/> 는 git 과 같이 첫 글자가 ' '(같음)·'-'(삭제)·'+'(추가)다.
/// 줄 번호는 1부터. 해당 쪽 줄이 없으면(새 파일·전부 삭제) Start 는 그 앞 줄 번호(0 가능)다.
/// </summary>
public sealed record DiffHunk(int OldStart, int OldLines, int NewStart, int NewLines, IReadOnlyList<string> Lines);

/// <summary>
/// 줄 단위 diff(Myers). /diff 의 턴별 변경처럼 git 이 모르는 두 텍스트를 비교할 때 쓴다.
/// 공통 앞·뒤 줄을 먼저 잘라 가운데만 비교하고, 그래도 너무 크면 최소 diff 대신 가운데를 통째 교체로 낸다
/// (결과는 여전히 정확하다 — 덜 촘촘할 뿐).
/// </summary>
public static class LineDiff
{
    public const int DefaultContext = 3;

    private const int MaxMiddleLines = 20_000;
    private const int MaxEdits = 2_000;

    /// <summary>줄로 나눈다(CRLF 는 LF 로). 끝의 개행 하나는 빈 줄로 세지 않는다.</summary>
    public static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    public static IReadOnlyList<DiffHunk> Compute(string? oldText, string? newText, int context = DefaultContext)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        return Hunks(Script(a, b), context);
    }

    private readonly record struct Op(char Kind, string Text);

    private static List<Op> Script(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
               && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }

        var ops = new List<Op>(a.Length + b.Length);
        for (var i = 0; i < prefix; i++)
        {
            ops.Add(new Op(' ', a[i]));
        }

        var midA = a[prefix..(a.Length - suffix)];
        var midB = b[prefix..(b.Length - suffix)];
        ops.AddRange(Myers(midA, midB) ?? Replace(midA, midB));

        for (var i = a.Length - suffix; i < a.Length; i++)
        {
            ops.Add(new Op(' ', a[i]));
        }

        return ops;
    }

    private static IEnumerable<Op> Replace(string[] a, string[] b)
        => a.Select(l => new Op('-', l)).Concat(b.Select(l => new Op('+', l)));

    // Myers O(ND). 단계마다 V 의 [-d-1, d+1] 구간을 저장해 두었다가 역추적한다. 한도를 넘으면 null.
    private static List<Op>? Myers(string[] a, string[] b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0)
        {
            return Replace(a, b).ToList();
        }

        if (n + m > MaxMiddleLines)
        {
            return null;
        }

        var max = n + m;
        var v = new int[2 * max + 3];
        var off = max + 1;
        var trace = new List<int[]>();
        var found = false;

        for (var d = 0; d <= Math.Min(max, MaxEdits) && !found; d++)
        {
            var snap = new int[2 * d + 3];
            Array.Copy(v, off - d - 1, snap, 0, snap.Length);
            trace.Add(snap);

            for (var k = -d; k <= d; k += 2)
            {
                var x = k == -d || (k != d && v[off + k - 1] < v[off + k + 1])
                    ? v[off + k + 1]
                    : v[off + k - 1] + 1;
                var y = x - k;
                while (x < n && y < m && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                v[off + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            return null;
        }

        var rev = new List<Op>();
        int cx = n, cy = m;
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var snap = trace[d];
            int At(int k) => snap[k + d + 1];
            var k = cx - cy;
            var prevK = k == -d || (k != d && At(k - 1) < At(k + 1)) ? k + 1 : k - 1;
            var prevX = d == 0 ? 0 : At(prevK);
            var prevY = prevX - prevK;
            if (d == 0)
            {
                prevX = 0;
                prevY = 0;
            }

            while (cx > prevX && cy > prevY)
            {
                rev.Add(new Op(' ', a[--cx]));
                cy--;
            }

            if (d > 0)
            {
                if (cx == prevX)
                {
                    rev.Add(new Op('+', b[--cy]));
                }
                else
                {
                    rev.Add(new Op('-', a[--cx]));
                }
            }
        }

        rev.Reverse();
        return rev;
    }

    private static List<DiffHunk> Hunks(List<Op> ops, int context)
    {
        // 각 연산 앞까지의 옛·새 줄 수(줄 번호 계산용).
        var oldBefore = new int[ops.Count + 1];
        var newBefore = new int[ops.Count + 1];
        for (var i = 0; i < ops.Count; i++)
        {
            oldBefore[i + 1] = oldBefore[i] + (ops[i].Kind == '+' ? 0 : 1);
            newBefore[i + 1] = newBefore[i] + (ops[i].Kind == '-' ? 0 : 1);
        }

        var hunks = new List<DiffHunk>();
        var i0 = 0;
        while (true)
        {
            var first = ops.FindIndex(i0, o => o.Kind != ' ');
            if (first < 0)
            {
                break;
            }

            var start = Math.Max(i0, first - context);
            var last = first;
            for (var j = first + 1; j < ops.Count; j++)
            {
                if (ops[j].Kind == ' ')
                {
                    continue;
                }

                if (j - last > 2 * context)
                {
                    break;   // 사이가 넓으면 다음 덩어리
                }

                last = j;
            }

            var end = Math.Min(ops.Count, last + context + 1);
            var oldLines = oldBefore[end] - oldBefore[start];
            var newLines = newBefore[end] - newBefore[start];
            hunks.Add(new DiffHunk(
                oldLines > 0 ? oldBefore[start] + 1 : oldBefore[start], oldLines,
                newLines > 0 ? newBefore[start] + 1 : newBefore[start], newLines,
                ops.GetRange(start, end - start).Select(o => o.Kind + o.Text).ToList()));
            i0 = end;
        }

        return hunks;
    }
}
