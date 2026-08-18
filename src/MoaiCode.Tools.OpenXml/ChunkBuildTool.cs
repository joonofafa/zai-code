using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 로컬 폴더/파일의 문서를 텍스트 추출·청킹해 대상 폴더 아래 <c>.moai-chunks/</c> 사이드카에 저장한다(쓰기).
/// 서버 무관·오프라인. 변경 안 된 파일은 건너뛴다(증분). 가져오기는 ChunkFetch.
/// </summary>
public sealed class ChunkBuildTool : ITool
{
    public string Name => "ChunkBuild";

    public string Description => """
        Pre-chunks local documents for offline retrieval. Extracts text from a file or a directory of
        documents (.txt/.md/.csv/.docx/.xlsx/.pptx/.pdf), splits it into overlapping chunks, and writes
        them into a `.moai-chunks/` sidecar next to the source (originals untouched). Server-independent.
        Unchanged files are skipped (incremental). Use `recursive: true` to include subdirectories.
        Retrieve the chunks later with ChunkFetch. This is local keyword/plain-text chunking — for
        semantic search use ChunkSearch over the locally stored vectors.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "File or directory to chunk (relative to workspace)" },
            "recursive": { "type": "boolean", "description": "Recurse into subdirectories (default false)" },
            "chunkSize": { "type": "integer", "description": "Max characters per chunk (default 1000)" },
            "overlap": { "type": "integer", "description": "Overlap characters between chunks (default 150)" },
            "force": { "type": "boolean", "description": "Rebuild even unchanged files (default false)" }
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("recursive")] bool? Recursive,
        [property: JsonPropertyName("chunkSize")] int? ChunkSize,
        [property: JsonPropertyName("overlap")] int? Overlap,
        [property: JsonPropertyName("force")] bool? Force);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput(L10n.Get("tools.chunkBuild.pathRequired"), IsError: true);
            yield break;
        }

        var full = "";
        string? pathError = null;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(inp.Path)
                ? inp.Path
                : System.IO.Path.Combine(context.WorkingDirectory, inp.Path));
        }
        catch (Exception ex)
        {
            pathError = ex.Message;
        }

        if (pathError is not null)
        {
            yield return new ToolOutput(L10n.Get("tools.chunkBuild.badPath", pathError), IsError: true);
            yield break;
        }

        // 앵커: 디렉토리면 그 자체, 파일이면 부모. 사이드카·상대경로 기준.
        string anchor;
        List<string> files;
        if (Directory.Exists(full))
        {
            anchor = full;
            var opt = inp.Recursive == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files = Directory.EnumerateFiles(anchor, "*", opt)
                .Where(f => DocumentTextExtractor.IsSupported(f)
                            && !f.Contains(System.IO.Path.DirectorySeparatorChar + ChunkStore.DirName + System.IO.Path.DirectorySeparatorChar)
                            && !System.IO.Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(full))
        {
            anchor = System.IO.Path.GetDirectoryName(full)!;
            files = DocumentTextExtractor.IsSupported(full) ? new List<string> { full } : new List<string>();
        }
        else
        {
            yield return new ToolOutput(L10n.Get("tools.chunkBuild.pathNotFound", inp.Path), IsError: true);
            yield break;
        }

        if (files.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.chunkBuild.noDocs"), IsError: true);
            yield break;
        }

        var store = new ChunkStore(anchor);
        var manifest = store.LoadManifest();

        var size = inp.ChunkSize is { } cs and > 0 ? cs : manifest.ChunkSize;
        var overlap = inp.Overlap is { } ov and >= 0 ? ov : manifest.Overlap;
        // 청크 파라미터가 바뀌면 증분 스킵이 부정확 → 전체 재빌드.
        var force = inp.Force == true || size != manifest.ChunkSize || overlap != manifest.Overlap;

        var bySource = manifest.Documents.ToDictionary(d => d.Source, StringComparer.Ordinal);
        var built = 0;
        var skipped = 0;
        var totalChunks = 0;
        var failures = new List<string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = System.IO.Path.GetRelativePath(anchor, file);
            var fi = new FileInfo(file);
            var mtime = fi.LastWriteTimeUtc.Ticks;

            if (!force && bySource.TryGetValue(rel, out var prev) && prev.Size == fi.Length && prev.MTimeUtcTicks == mtime)
            {
                skipped++;
                totalChunks += prev.Chunks;
                continue;
            }

            string text;
            try
            {
                text = DocumentTextExtractor.Extract(file);
            }
            catch (Exception ex)
            {
                failures.Add($"{rel} ({ex.GetType().Name})");
                continue;
            }

            var chunks = TextChunker.Chunk(text, size, overlap);
            var relFile = store.WriteChunks(rel, chunks);
            bySource[rel] = new ChunkDocEntry(rel, fi.Length, mtime, chunks.Count, relFile);
            built++;
            totalChunks += chunks.Count;
        }

        var updated = new ChunkManifest(size, overlap, bySource.Values.OrderBy(d => d.Source, StringComparer.Ordinal).ToList());
        store.SaveManifest(updated);

        yield return new ToolOutput(Render(store.ChunksDir, built, skipped, totalChunks, size, overlap, failures));
    }

    private static string Render(
        string chunksDir, int built, int skipped, int totalChunks, int size, int overlap, List<string> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L10n.Get("tools.chunkBuild.resultSummary", built, skipped, totalChunks));
        sb.AppendLine(L10n.Get("tools.chunkBuild.resultParams", size, overlap, chunksDir));
        if (failures.Count > 0)
        {
            sb.AppendLine(L10n.Get("tools.chunkBuild.resultFailures", failures.Count, string.Join(", ", failures)));
        }

        sb.Append(L10n.Get("tools.chunkBuild.resultFooter"));
        return sb.ToString();
    }
}
