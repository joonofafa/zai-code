using System.Text.Json;
using MoaiCode.Core.Diff;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.Files;
using MoaiCode.Tui.Diff;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>/diff(Claude Code 이식): 줄 diff, 턴별 변경 기록, git diff 해석.</summary>
public sealed class DiffCommandTests
{
    // 덩어리를 옛 텍스트에 적용해 새 텍스트를 다시 만든다 — 줄 번호와 내용이 모두 맞아야 같아진다.
    private static string Apply(string oldText, IReadOnlyList<DiffHunk> hunks)
    {
        var a = LineDiff.SplitLines(oldText);
        var result = new List<string>();
        var pos = 0;   // 0 기반 옛 줄 위치
        foreach (var h in hunks)
        {
            var start = h.OldLines == 0 ? h.OldStart : h.OldStart - 1;
            while (pos < start)
            {
                result.Add(a[pos++]);
            }

            foreach (var line in h.Lines)
            {
                switch (line[0])
                {
                    case ' ':
                        Assert.Equal(a[pos], line[1..]);
                        result.Add(a[pos++]);
                        break;
                    case '-':
                        Assert.Equal(a[pos], line[1..]);
                        pos++;
                        break;
                    default:
                        result.Add(line[1..]);
                        break;
                }
            }
        }

        while (pos < a.Length)
        {
            result.Add(a[pos++]);
        }

        return string.Join("\n", result);
    }

    [Theory]
    [InlineData("a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n", "a\nB\nc\nd\ne\nf\ng\nh\nI\nj\n")]
    [InlineData("", "x\ny\n")]
    [InlineData("x\ny\n", "")]
    [InlineData("1\n2\n3\n", "0\n1\n2\n3\n4\n")]
    [InlineData("same\n", "same\n")]
    [InlineData("a\nb\na\nb\nc\n", "b\na\nc\nb\na\n")]
    public void Hunks_rebuild_the_new_text(string oldText, string newText)
    {
        var hunks = LineDiff.Compute(oldText, newText);
        Assert.Equal(string.Join("\n", LineDiff.SplitLines(newText)), Apply(oldText, hunks));
    }

    [Fact]
    public void Distant_changes_become_separate_hunks_with_context()
    {
        var old = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line{i}"));
        var @new = old.Replace("line3\n", "line3!\n").Replace("line25\n", "line25!\n");

        var hunks = LineDiff.Compute(old, @new);

        Assert.Equal(2, hunks.Count);
        Assert.Equal((1, 6, 1, 6), (hunks[0].OldStart, hunks[0].OldLines, hunks[0].NewStart, hunks[0].NewLines));
        Assert.Equal(["-line3", "+line3!"], hunks[0].Lines.Where(l => l[0] != ' '));
        Assert.Equal(22, hunks[1].OldStart);
    }

    [Fact]
    public void Huge_middles_fall_back_to_a_correct_replace()
    {
        var old = string.Join("\n", Enumerable.Range(0, 15_000).Select(i => $"o{i}"));
        var @new = string.Join("\n", Enumerable.Range(0, 15_000).Select(i => $"n{i}"));
        var hunks = LineDiff.Compute(old, @new);
        Assert.Equal(@new, Apply(old, hunks));
    }

    [Fact]
    public void Journal_keeps_first_before_and_last_after_per_turn()
    {
        var j = new FileChangeJournal();
        var path = Path.Combine(Path.GetTempPath(), "j.txt");
        j.BeginTurn("first request");
        j.Record(path, "v1", "v2");
        j.Record(path, "v2", "v3");
        j.BeginTurn("no edits");
        j.BeginTurn("third\nmore lines");
        j.Record(path, "v3", "v4");
        j.Record(path, "v4", "v3");   // 되돌림 — 이 턴엔 변경 없음

        var turns = j.Snapshot();

        var t = Assert.Single(turns);
        Assert.Equal(1, t.Index);
        Assert.Equal("first request", t.Prompt);
        var f = Assert.Single(t.Files);
        Assert.Equal(("v1", "v3", false), (f.Before, f.After, f.IsNew));

        j.Clear();
        Assert.Empty(j.Snapshot());
    }

    [Fact]
    public void Journal_lists_recent_turns_first_and_marks_new_and_large_files()
    {
        var j = new FileChangeJournal();
        j.BeginTurn("one");
        j.Record("/tmp/a", null, "new");
        j.BeginTurn("two");
        j.Record("/tmp/b", "x", new string('y', FileChangeJournal.MaxContentChars + 1));

        var turns = j.Snapshot();

        Assert.Equal([2, 1], turns.Select(t => t.Index));
        Assert.True(turns[0].Files[0].IsLarge);
        Assert.Null(turns[0].Files[0].After);
        Assert.True(turns[1].Files[0].IsNew);
    }

    [Fact]
    public async Task Edit_and_write_record_into_the_journal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zai-diff-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var j = new FileChangeJournal();
            j.BeginTurn("make changes");
            var reads = new ReadTracker();
            var ctx = new ToolContext(dir, PermissionMode.Auto, reads, Changes: j);
            var file = Path.Combine(dir, "a.txt");

            await Run(new FileWriteTool(), $$"""{"path":{{JsonSerializer.Serialize(file)}},"content":"one\ntwo\n"}""", ctx);
            await Run(new FileEditTool(), $$"""{"path":{{JsonSerializer.Serialize(file)}},"old_string":"two","new_string":"TWO"}""", ctx);

            var change = Assert.Single(Assert.Single(j.Snapshot()).Files);
            Assert.True(change.IsNew);
            Assert.Null(change.Before);
            Assert.Equal("one\nTWO\n", change.After);

            var view = DiffModel.FromTurn(j.Snapshot()[0], dir);
            var f = Assert.Single(view.Files);
            Assert.Equal("a.txt", f.Path);
            Assert.Equal((2, 0), (f.Added, f.Removed));
            Assert.Equal("\"make changes\"", view.Subtitle);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Git_diff_output_is_parsed_per_file()
    {
        var diff = string.Join("\n",
            "diff --git a/src/x.cs b/src/x.cs",
            "index 1..2 100644",
            "--- a/src/x.cs",
            "+++ b/src/x.cs",
            "@@ -1,3 +1,3 @@",
            " keep",
            "---legacy-peer-deps",      // 내용이 "--" 로 시작하는 삭제 줄 — 헤더로 버리면 안 된다
            "+++new-flag",
            " end",
            "\\ No newline at end of file",
            "diff --git a/gone.txt b/gone.txt",
            "deleted file mode 100644",
            "--- a/gone.txt",
            "+++ /dev/null",
            "@@ -1 +0,0 @@",
            "-bye",
            "diff --git \"a/\\355\\225\\234.txt\" \"b/\\355\\225\\234.txt\"",
            "--- \"a/\\355\\225\\234.txt\"",
            "+++ \"b/\\355\\225\\234.txt\"",
            "@@ -0,0 +1 @@",
            "+hi",
            "");

        var files = GitWorkingDiff.ParseDiff(diff);

        Assert.Equal(["--legacy-peer-deps", "++new-flag"],
            files["src/x.cs"].Hunks[0].Lines.Where(l => l[0] != ' ').Select(l => l[1..]));
        Assert.Equal((1, 1, 1, 0), (files["gone.txt"].Hunks[0].OldStart, files["gone.txt"].Hunks[0].OldLines, 1, files["gone.txt"].Hunks[0].NewLines));
        Assert.True(files.ContainsKey("한.txt"));
    }

    [Fact]
    public void Numstat_and_shortstat_are_parsed()
    {
        var rows = GitWorkingDiff.ParseNumstat("3\t1\tsrc/a.cs\n-\t-\timg.png\n").ToList();
        Assert.Equal(("src/a.cs", 3, 1, false), rows[0]);
        Assert.Equal(("img.png", 0, 0, true), rows[1]);
        Assert.Equal((2, 5, 0), GitWorkingDiff.ParseShortstat(" 2 files changed, 5 insertions(+)\n"));
        Assert.Equal((1, 0, 4), GitWorkingDiff.ParseShortstat(" 1 file changed, 4 deletions(-)\n"));
    }

    [Fact]
    public void Working_tree_view_lists_changed_and_untracked_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zai-git-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            void Git(params string[] args)
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args)
                {
                    psi.ArgumentList.Add(a);
                }

                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }

            Git("init", "-q");
            Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "root");
            File.WriteAllText(Path.Combine(dir, "a.txt"), "one\n");
            Git("add", "a.txt");
            Git("-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "a");
            File.WriteAllText(Path.Combine(dir, "a.txt"), "one\ntwo\n");
            File.WriteAllText(Path.Combine(dir, "b.txt"), "new\nfile\n");

            var view = GitWorkingDiff.Load(dir);

            Assert.Equal(["a.txt", "b.txt"], view.Files.Select(f => f.Path));
            Assert.Equal((1, 0), (view.Files[0].Added, view.Files[0].Removed));
            Assert.True(view.Files[1].IsUntracked);
            Assert.Equal(2, view.Files[1].Added);   // 작은 텍스트 새 파일은 내용을 추가 줄로 보여 준다
            Assert.Equal(3, view.Added);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Outside_git_the_view_says_so()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zai-nogit-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var view = GitWorkingDiff.Load(dir);
            Assert.Empty(view.Files);
            Assert.Equal(MoaiCode.Localization.L10n.Get("diff.empty.notGit"), view.Empty);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    private static async Task Run(ITool tool, string json, ToolContext ctx)
    {
        using var doc = JsonDocument.Parse(json);
        await foreach (var p in tool.ExecuteAsync(doc.RootElement, ctx, default))
        {
            if (p is ToolOutput { IsError: true } o)
            {
                Assert.Fail(o.Text);
            }
        }
    }
}
