using System.Text.Json;
using MoaiCode.Core.Tools;
using MoaiCode.Tools.OpenXml;
using Xunit;

namespace MoaiCode.OpenXml.Tests;

// ChunkBuild(청킹·저장) → ChunkFetch(가져오기) 로컬 파이프라인 + 추출/청킹 단위.
public sealed class ChunkToolsTests : IDisposable
{
    private readonly string _dir;

    public ChunkToolsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-chunk-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    private async Task<string> Run(ITool tool, object input, bool expectOk = true)
    {
        var ctx = new ToolContext(_dir, PermissionMode.Auto);
        var json = JsonSerializer.SerializeToElement(input);
        var sb = new System.Text.StringBuilder();
        await foreach (var p in tool.ExecuteAsync(json, ctx, CancellationToken.None))
        {
            if (p is ToolOutput o)
            {
                if (expectOk) { Assert.False(o.IsError, o.Text); }
                sb.Append(o.Text);
            }
        }

        return sb.ToString();
    }

    [Fact]
    public void Chunker_splits_long_text_with_overlap()
    {
        var chunks = TextChunker.Chunk(new string('a', 2500), maxChars: 1000, overlapChars: 150);
        Assert.True(chunks.Count >= 3, $"expected >=3, got {chunks.Count}");
        Assert.All(chunks, c => Assert.True(c.Length <= 1000));
    }

    [Fact]
    public async Task Extractor_reads_docx_text()
    {
        // OpenXml 로 docx 를 만들고 다시 텍스트 추출.
        var path = Path.Combine(_dir, "a.docx");
        await Run(new DocxCreateTool(),
            new { path = "a.docx", title = "제목", paragraphs = new[] { "본문 첫째 줄", "본문 둘째 줄" } });
        var text = DocumentTextExtractor.Extract(path);
        Assert.Contains("본문 첫째 줄", text);
        Assert.Contains("제목", text);
    }

    [Fact]
    public async Task Build_then_fetch_roundtrip()
    {
        var src = Directory.CreateDirectory(Path.Combine(_dir, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(src, "note.md"),
            "# 제목\n\n" + string.Join("\n", Enumerable.Repeat("이것은 청킹 테스트 문장입니다.", 80)));
        await File.WriteAllTextAsync(Path.Combine(src, "data.csv"), "a,b,c\n1,2,3\n4,5,6\n");

        // 빌드
        var build = await Run(new ChunkBuildTool(), new { path = "src" });
        Assert.Contains("새로 청킹 2개", build);
        Assert.True(Directory.Exists(Path.Combine(src, ".moai-chunks")));

        // 개요(디렉토리)
        var overview = await Run(new ChunkFetchTool(), new { path = "src" });
        Assert.Contains("note.md", overview);
        Assert.Contains("data.csv", overview);

        // 특정 문서 청크
        var chunks = await Run(new ChunkFetchTool(), new { path = "src", source = "note.md" });
        Assert.Contains("청킹 테스트 문장", chunks);
        Assert.StartsWith("<system-reminder>", chunks); // 문서 본문은 untrusted 경계

        // 증분: 변경 없이 재빌드하면 스킵
        var rebuild = await Run(new ChunkBuildTool(), new { path = "src" });
        Assert.Contains("변경없음 2개", rebuild);
    }

    [Fact]
    public async Task Recursive_flag_includes_subdirectories()
    {
        var src = Directory.CreateDirectory(Path.Combine(_dir, "root")).FullName;
        await File.WriteAllTextAsync(Path.Combine(src, "top.txt"), "top level content");
        var sub = Directory.CreateDirectory(Path.Combine(src, "sub")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub, "deep.txt"), "deep content");

        var top = await Run(new ChunkBuildTool(), new { path = "root" });
        Assert.Contains("새로 청킹 1개", top); // sub 제외

        var rec = await Run(new ChunkBuildTool(), new { path = "root", recursive = true });
        Assert.Contains("새로 청킹 1개", rec); // sub/deep.txt 추가(top.txt 는 변경없음)
        var overview = await Run(new ChunkFetchTool(), new { path = "root" });
        Assert.Contains(Path.Combine("sub", "deep.txt"), overview);
    }

    [Fact]
    public async Task Fetch_without_build_reports_missing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var text = await Run(new ChunkFetchTool(), new { path = "empty" }, expectOk: false);
        Assert.Contains("ChunkBuild", text); // 먼저 청킹하라는 안내
    }
}
