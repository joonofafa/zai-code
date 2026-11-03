using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using MoaiCode.Core.Diff;
using MoaiCode.Localization;

namespace MoaiCode.Tui.Diff;

/// <summary>
/// /diff 의 "현재" 화면: 커밋하지 않은 변경 전체(git diff HEAD) + 아직 추적하지 않는 새 파일.
/// merge·rebase·cherry-pick·revert 중에는 들어온 변경이 섞이므로 보여 주지 않는다(원본과 같음).
/// </summary>
internal static partial class GitWorkingDiff
{
    private const int MaxFiles = 50;
    private const int MaxFilesForDetails = 500;
    private const int MaxDiffBytesPerFile = 1_000_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static DiffView Load(string cwd)
    {
        var tab = L10n.Get("diff.tab.current");
        var title = L10n.Get("diff.current.title");
        var subtitle = L10n.Get("diff.current.subtitle");
        DiffView Empty(string message, int files = 0, int added = 0, int removed = 0)
            => new(tab, title, subtitle, [], files, added, removed, message);

        var top = Git(cwd, "rev-parse", "--show-toplevel");
        var gitDir = Git(cwd, "rev-parse", "--absolute-git-dir");
        if (top is not { Code: 0 } || gitDir is not { Code: 0 })
        {
            return Empty(L10n.Get("diff.empty.notGit"));
        }

        var root = top.Out.Trim();
        if (new[] { "MERGE_HEAD", "REBASE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" }
            .Any(f => File.Exists(Path.Combine(gitDir.Out.Trim(), f))))
        {
            return Empty(L10n.Get("diff.empty.transient"));
        }

        // 아주 큰 변경(수백 파일)은 합계만 — 전부 읽으면 메모리를 크게 쓴다.
        if (Git(root, "diff", "HEAD", "--shortstat") is { Code: 0 } shortstat
            && ParseShortstat(shortstat.Out) is { } quick && quick.Files > MaxFilesForDetails)
        {
            return Empty(L10n.Get("diff.empty.tooMany"), quick.Files, quick.Added, quick.Removed);
        }

        var files = new List<DiffFile>();
        if (Git(root, "diff", "HEAD", "--numstat", "--no-renames") is { Code: 0 } numstat)
        {
            var hunks = Git(root, "diff", "HEAD", "--no-renames", "--no-color", "--no-ext-diff") is { Code: 0 } full
                ? ParseDiff(full.Out)
                : new Dictionary<string, (IReadOnlyList<DiffHunk> Hunks, bool Truncated)>();
            foreach (var (path, added, removed, binary) in ParseNumstat(numstat.Out).Take(MaxFiles))
            {
                if (binary)
                {
                    files.Add(new DiffFile(path, 0, 0, [], IsBinary: true));
                }
                else if (hunks.TryGetValue(path, out var h))
                {
                    files.Add(new DiffFile(path, added, removed, h.Hunks, IsTruncated: h.Truncated));
                }
                else
                {
                    files.Add(new DiffFile(path, added, removed, [], IsLarge: true));
                }
            }
        }

        // 추적하지 않는 새 파일. 원본은 이름만 보여 주지만, 작은 텍스트 파일은 내용을 추가된 줄로 보여 준다.
        if (files.Count < MaxFiles && Git(root, "ls-files", "--others", "--exclude-standard", "--full-name") is { Code: 0 } untracked)
        {
            foreach (var path in untracked.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(MaxFiles - files.Count))
            {
                files.Add(Untracked(root, path.TrimEnd('\r')));
            }
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new DiffView(tab, title, subtitle, files, files.Count,
            files.Sum(f => f.Added), files.Sum(f => f.Removed), L10n.Get("diff.empty.clean"));
    }

    private static DiffFile Untracked(string root, string path)
    {
        try
        {
            var full = Path.Combine(root, path);
            var info = new FileInfo(full);
            if (info.Length <= MaxDiffBytesPerFile)
            {
                var bytes = File.ReadAllBytes(full);
                if (Array.IndexOf(bytes, (byte)0) < 0 && TryUtf8(bytes) is { } text)
                {
                    var hunks = LineDiff.Compute(null, text);
                    var (kept, truncated) = DiffModel.Limit(hunks);
                    return new DiffFile(path, DiffModel.Count(hunks).Added, 0, kept,
                        IsTruncated: truncated, IsUntracked: true, IsNew: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 이름만 보여 준다.
        }

        return new DiffFile(path, 0, 0, [], IsUntracked: true, IsNew: true);
    }

    // 엄격한 UTF-8(BOM 허용). 다른 인코딩이면 null — 이름만 보여 준다.
    private static string? TryUtf8(byte[] bytes)
    {
        try
        {
            var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    internal static (int Files, int Added, int Removed)? ParseShortstat(string text)
    {
        var m = ShortstatRegex().Match(text);
        if (!m.Success)
        {
            return null;
        }

        static int N(Group g) => g.Success ? int.Parse(g.Value) : 0;
        return (N(m.Groups[1]), N(m.Groups[2]), N(m.Groups[3]));
    }

    /// <summary>git diff --numstat: "추가\t삭제\t경로", 바이너리는 "-\t-\t경로".</summary>
    internal static IEnumerable<(string Path, int Added, int Removed, bool Binary)> ParseNumstat(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t', 3);
            if (parts.Length != 3)
            {
                continue;
            }

            var binary = parts[0] == "-" || parts[1] == "-";
            yield return (Unquote(parts[2]), binary ? 0 : int.Parse(parts[0]), binary ? 0 : int.Parse(parts[1]), binary);
        }
    }

    /// <summary>git diff 원문 → 파일별 덩어리. 파일 diff 가 1MB 를 넘으면 건너뛴다(목록에는 '큰 파일'로 남는다).</summary>
    internal static Dictionary<string, (IReadOnlyList<DiffHunk> Hunks, bool Truncated)> ParseDiff(string text)
    {
        var result = new Dictionary<string, (IReadOnlyList<DiffHunk>, bool)>(StringComparer.Ordinal);
        foreach (var chunk in FileHeaderRegex().Split(text).Where(c => c.Length > 0))
        {
            if (result.Count >= MaxFiles)
            {
                break;
            }

            if (Encoding.UTF8.GetByteCount(chunk) > MaxDiffBytesPerFile)
            {
                continue;
            }

            var lines = chunk.Replace("\r\n", "\n").Split('\n');
            string? path = null;
            var hunks = new List<DiffHunk>();
            List<string>? current = null;
            int oldStart = 0, oldLines = 0, newStart = 0, newLines = 0;
            var inHunk = false;

            void Close()
            {
                if (current is not null)
                {
                    hunks.Add(new DiffHunk(oldStart, oldLines, newStart, newLines, current));
                }

                current = null;
            }

            foreach (var line in lines)
            {
                if (!inHunk && line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var p = Unquote(line[4..]);
                    if (p != "/dev/null")   // 삭제된 파일은 +++ 가 /dev/null — --- 쪽 경로를 쓴다
                    {
                        path = p.StartsWith("b/", StringComparison.Ordinal) ? p[2..] : p;
                    }

                    continue;
                }

                if (!inHunk && line.StartsWith("--- ", StringComparison.Ordinal) && path is null)
                {
                    var p = Unquote(line[4..]);
                    if (p != "/dev/null")
                    {
                        path = p.StartsWith("a/", StringComparison.Ordinal) ? p[2..] : p;
                    }

                    continue;
                }

                var m = HunkHeaderRegex().Match(line);
                if (m.Success)
                {
                    Close();
                    inHunk = true;
                    oldStart = int.Parse(m.Groups[1].Value);
                    oldLines = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 1;
                    newStart = int.Parse(m.Groups[3].Value);
                    newLines = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 1;
                    current = new List<string>();
                    continue;
                }

                // "--" 로 시작하는 삭제 줄("---legacy" 등)을 헤더로 버리지 않도록, 덩어리 안에서는 첫 글자만 본다.
                if (inHunk && current is not null && line.Length > 0 && line[0] is ' ' or '+' or '-')
                {
                    current.Add(line);
                }
            }

            Close();
            if (path is not null && path != "/dev/null")
            {
                var limited = DiffModel.Limit(hunks);
                result[path] = (limited.Hunks, limited.Truncated);
            }
        }

        return result;
    }

    // core.quotepath 로 감싼 경로("a/\355\225\234.txt")를 푼다. 감싸지 않았으면 그대로.
    private static string Unquote(string s)
    {
        s = s.TrimEnd('\t');
        if (s.Length < 2 || s[0] != '"' || s[^1] != '"')
        {
            return s;
        }

        var bytes = new List<byte>();
        for (var i = 1; i < s.Length - 1; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length - 1)
            {
                var n = s[++i];
                if (n is >= '0' and <= '7' && i + 2 < s.Length - 1)
                {
                    bytes.Add(Convert.ToByte(s.Substring(i, 3), 8));
                    i += 2;
                    continue;
                }

                bytes.Add((byte)(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => n }));
                continue;
            }

            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed record GitResult(int Code, string Out);

    private static GitResult? Git(string cwd, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("--no-optional-locks");   // 읽기만 하는데 index.lock 을 잡지 않게
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var stdout = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Timeout))
            {
                try
                {
                    p.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return null;
            }

            return new GitResult(p.ExitCode, stdout.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;   // git 이 없다
        }
    }

    [GeneratedRegex(@"(\d+) files? changed(?:, (\d+) insertions?\(\+\))?(?:, (\d+) deletions?\(-\))?")]
    private static partial Regex ShortstatRegex();

    [GeneratedRegex(@"^diff --git ", RegexOptions.Multiline)]
    private static partial Regex FileHeaderRegex();

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();
}
