using MoaiCode.Cli;
using Xunit;

namespace MoaiCode.Core.Tests;

// 리포맵은 시작 경로에서 동기로 만들어진다. 홈처럼 심볼릭 링크가 걸린 트리에서 링크를 따라가면
// 네트워크 마운트(rclone gDrive 등)로 새어나가 시작 자체가 끝나지 않는다 — 그래서 FileWalker 경유.
public sealed class RepoMapBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "repomap-" + Guid.NewGuid().ToString("N")[..8]);

    public RepoMapBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Maps_symbols_from_source_files()
    {
        File.WriteAllText(Path.Combine(_root, "Sample.cs"), "public class Widget\n{\n}\n");
        var map = RepoMapBuilder.Build(_root, 4000);
        Assert.NotNull(map);
        Assert.Contains("Sample.cs", map);
        Assert.Contains("Widget", map);
    }

    [Fact]
    public void Does_not_follow_directory_symlinks()
    {
        // 링크 대상에만 있는 심볼은 맵에 들어오면 안 된다(링크를 따라갔다는 뜻이므로).
        var outside = Path.Combine(Path.GetTempPath(), "repomap-out-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "Linked.cs"), "public class OnlyBehindTheLink\n{\n}\n");
            File.WriteAllText(Path.Combine(_root, "Real.cs"), "public class InsideRoot\n{\n}\n");
            Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);

            var map = RepoMapBuilder.Build(_root, 4000);

            Assert.NotNull(map);
            Assert.Contains("InsideRoot", map);
            Assert.DoesNotContain("OnlyBehindTheLink", map);
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Returns_null_for_a_missing_root_or_no_budget()
    {
        Assert.Null(RepoMapBuilder.Build(Path.Combine(_root, "nope"), 4000));
        Assert.Null(RepoMapBuilder.Build(_root, 0));
    }
}
