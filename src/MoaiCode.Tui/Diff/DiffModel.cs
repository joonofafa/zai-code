using MoaiCode.Core.Diff;
using MoaiCode.Localization;

namespace MoaiCode.Tui.Diff;

/// <summary>/diff 목록의 파일 하나. Hunks 는 표시 한도(<see cref="DiffModel.MaxLinesPerFile"/>)까지만 담는다.</summary>
internal sealed record DiffFile(
    string Path,
    int Added,
    int Removed,
    IReadOnlyList<DiffHunk> Hunks,
    bool IsBinary = false,
    bool IsLarge = false,
    bool IsTruncated = false,
    bool IsUntracked = false,
    bool IsNew = false);

/// <summary>/diff 의 한 화면(현재 변경 또는 턴 하나). Files 가 비면 Empty 를 보여 준다.</summary>
internal sealed record DiffView(
    string Tab,
    string Title,
    string? Subtitle,
    IReadOnlyList<DiffFile> Files,
    int FilesCount,
    int Added,
    int Removed,
    string Empty);

internal static class DiffModel
{
    /// <summary>파일 하나에서 보여 줄 최대 diff 줄 수. 넘으면 잘랐다고 표시한다.</summary>
    public const int MaxLinesPerFile = 1_000;

    /// <summary>턴 기록 하나를 화면으로. 경로는 cwd 아래면 상대 경로로 보인다.</summary>
    public static DiffView FromTurn(TurnChanges turn, string cwd)
    {
        var files = turn.Files
            .Select(f =>
            {
                var path = Relative(f.Path, cwd);
                if (f.IsLarge)
                {
                    return new DiffFile(path, 0, 0, [], IsLarge: true, IsNew: f.IsNew);
                }

                var hunks = LineDiff.Compute(f.Before, f.After);
                var (added, removed) = Count(hunks);
                var (kept, truncated) = Limit(hunks);
                return new DiffFile(path, added, removed, kept, IsTruncated: truncated, IsNew: f.IsNew);
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        var preview = Preview(turn.Prompt);
        return new DiffView(
            L10n.Get("diff.tab.turn", turn.Index),
            L10n.Get("diff.turn.title", turn.Index),
            preview.Length > 0 ? $"\"{preview}\"" : null,
            files,
            files.Count,
            files.Sum(f => f.Added),
            files.Sum(f => f.Removed),
            L10n.Get("diff.empty.turn"));
    }

    public static (int Added, int Removed) Count(IEnumerable<DiffHunk> hunks)
    {
        int added = 0, removed = 0;
        foreach (var line in hunks.SelectMany(h => h.Lines))
        {
            if (line.StartsWith('+'))
            {
                added++;
            }
            else if (line.StartsWith('-'))
            {
                removed++;
            }
        }

        return (added, removed);
    }

    /// <summary>표시 한도까지만 남긴다. 한도에 걸린 덩어리는 앞부분만 남기고 줄 수를 그에 맞춘다.</summary>
    public static (IReadOnlyList<DiffHunk> Hunks, bool Truncated) Limit(IReadOnlyList<DiffHunk> hunks)
    {
        var kept = new List<DiffHunk>();
        var budget = MaxLinesPerFile;
        foreach (var h in hunks)
        {
            if (budget <= 0)
            {
                return (kept, true);
            }

            if (h.Lines.Count <= budget)
            {
                kept.Add(h);
                budget -= h.Lines.Count;
                continue;
            }

            var lines = h.Lines.Take(budget).ToList();
            kept.Add(h with
            {
                Lines = lines,
                OldLines = lines.Count(l => !l.StartsWith('+')),
                NewLines = lines.Count(l => !l.StartsWith('-')),
            });
            return (kept, true);
        }

        return (kept, false);
    }

    /// <summary>요청 미리보기(원본처럼 30자 남짓).</summary>
    public static string Preview(string prompt) => prompt.Length <= 30 ? prompt : prompt[..29] + "…";

    public static string Relative(string path, string cwd)
    {
        try
        {
            var rel = System.IO.Path.GetRelativePath(cwd, path);
            return rel.StartsWith("..", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(rel) ? path : rel;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
