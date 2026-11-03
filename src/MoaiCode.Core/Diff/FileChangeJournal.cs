namespace MoaiCode.Core.Diff;

/// <summary>한 턴에 바뀐 파일 하나. 그 턴의 첫 변경 전 내용과 마지막 변경 후 내용. 너무 크면 내용 없이 IsLarge.</summary>
public sealed record FileChange(string Path, string? Before, string? After, bool IsNew, bool IsLarge);

/// <summary>파일을 바꾼 한 턴(사용자 요청 하나). Index 는 이 실행에서 1부터 센 요청 번호다.</summary>
public sealed record TurnChanges(int Index, string Prompt, IReadOnlyList<FileChange> Files);

/// <summary>
/// /diff 의 턴별 변경 기록. Edit·Write 가 파일을 쓸 때마다 전·후 내용을 남기고, REPL 이 요청마다 턴을 연다.
/// 서브에이전트도 같은 기록부를 받아 그 턴에 함께 쌓인다. 셸 명령으로 바꾼 파일은 여기 없다(현재 변경 git diff 에는 보인다).
/// 이 실행 동안만 메모리에 둔다.
/// </summary>
public sealed class FileChangeJournal
{
    public const int MaxContentChars = 1_000_000;
    private const int MaxTurns = 100;

    private readonly object _lock = new();
    private readonly List<Turn> _turns = new();
    private int _lastIndex;

    private sealed class Turn(int index, string prompt)
    {
        public int Index { get; } = index;
        public string Prompt { get; } = prompt;
        public Dictionary<string, Entry> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Order { get; } = new();
    }

    private sealed class Entry
    {
        public string? Before;
        public string? After;
        public bool IsNew;
        public bool IsLarge;
    }

    /// <summary>새 요청을 연다. 이후 기록은 이 턴에 쌓인다.</summary>
    public void BeginTurn(string prompt)
    {
        lock (_lock)
        {
            _turns.Add(new Turn(++_lastIndex, FirstLine(prompt)));
            if (_turns.Count > MaxTurns)
            {
                _turns.RemoveAt(0);
            }
        }
    }

    /// <summary>파일 쓰기 하나를 기록한다. <paramref name="before"/> 가 null 이면 새로 만든 파일이다.</summary>
    public void Record(string path, string? before, string after)
    {
        var full = Path.GetFullPath(path);
        lock (_lock)
        {
            if (_turns.Count == 0)
            {
                _turns.Add(new Turn(++_lastIndex, ""));   // 헤드리스 등 턴을 열지 않은 실행
            }

            var turn = _turns[^1];
            if (!turn.Files.TryGetValue(full, out var e))
            {
                e = new Entry { Before = before, IsNew = before is null };
                turn.Files[full] = e;
                turn.Order.Add(full);
            }

            e.After = after;
            if ((e.Before?.Length ?? 0) > MaxContentChars || after.Length > MaxContentChars)
            {
                e.IsLarge = true;
            }

            if (e.IsLarge)
            {
                e.Before = null;
                e.After = null;
            }
        }
    }

    /// <summary>파일이 실제로 바뀐 턴만, 최근 것부터. 턴 안에서 되돌려 원래대로 돌아간 파일은 빠진다.</summary>
    public IReadOnlyList<TurnChanges> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<TurnChanges>();
            for (var i = _turns.Count - 1; i >= 0; i--)
            {
                var t = _turns[i];
                var files = t.Order
                    .Select(p => (Path: p, E: t.Files[p]))
                    .Where(f => f.E.IsLarge || f.E.IsNew || f.E.Before != f.E.After)
                    .Select(f => new FileChange(f.Path, f.E.Before, f.E.After, f.E.IsNew, f.E.IsLarge))
                    .ToList();
                if (files.Count > 0)
                {
                    result.Add(new TurnChanges(t.Index, t.Prompt, files));
                }
            }

            return result;
        }
    }

    /// <summary>/clear — 대화를 비우면 턴 기록도 비운다.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _turns.Clear();
            _lastIndex = 0;
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length <= 200 ? line : line[..200];
    }
}
