using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 로컬 폴더의 문서를 인덱싱한다: 로컬 청킹(.moai-chunks/*.jsonl) + 서버 임베딩(/v1/embeddings, compute-only)
/// → 로컬 벡터(*.vec + vectors.json). 원본 문서는 서버에 저장되지 않는다(임베딩만). 이후 LocalDocsSearch 로
/// 오프라인 코사인 검색. 증분: 크기+수정시각으로 미변경 문서는 건너뛰고, 벡터가 없거나 stale 인 것만 임베딩.
/// </summary>
public sealed class LocalIndexBuildTool : ITool
{
    public string Name => "LocalIndexBuild";

    public string Description => """
        Indexes local documents for offline semantic search WITHOUT uploading the files to the server.
        Chunks each document locally, embeds the chunks via the server's compute-only embeddings endpoint
        (text transits only to embed; nothing is stored server-side), and writes local vectors to
        `.moai-chunks/`. `path` may be a file or directory (recursive with `recursive:true`). Incremental:
        unchanged documents (same size+mtime with up-to-date vectors) are skipped. Use LocalDocsSearch to
        query the index afterward. Supported types: .txt/.md/.csv/.docx/.xlsx/.pptx/.pdf.
        """;

    public bool IsReadOnly => false;
    public bool IsConcurrencySafe => false;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Local file OR directory to index" },
            "recursive": { "type": "boolean", "description": "Recurse into subdirectories (default false)" },
            "chunkSize": { "type": "integer", "description": "Chunk size in characters (default 1000)" },
            "overlap": { "type": "integer", "description": "Chunk overlap in characters (default 200)" },
            "force": { "type": "boolean", "description": "Re-chunk and re-embed everything (default false)" }
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
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("LocalIndexBuild: 'path'(파일 또는 디렉토리)가 필요합니다.", IsError: true);
            yield break;
        }

        var full = string.Empty;
        string? pathError = null;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(inp.Path) ? inp.Path : Path.Combine(context.WorkingDirectory, inp.Path));
        }
        catch (Exception ex)
        {
            pathError = ex.Message;
        }

        if (pathError is not null)
        {
            yield return new ToolOutput($"LocalIndexBuild: 잘못된 경로 — {pathError}", IsError: true);
            yield break;
        }

        string anchor;
        List<string> files;
        if (Directory.Exists(full))
        {
            anchor = full;
            var opt = inp.Recursive == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files = Directory.EnumerateFiles(anchor, "*", opt)
                .Where(f => DocumentTextExtractor.IsSupported(f)
                            && !f.Contains(Path.DirectorySeparatorChar + ChunkStore.DirName + Path.DirectorySeparatorChar)
                            && !Path.GetFileName(f).StartsWith('.'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(full))
        {
            anchor = Path.GetDirectoryName(full)!;
            files = DocumentTextExtractor.IsSupported(full) ? new List<string> { full } : new List<string>();
        }
        else
        {
            yield return new ToolOutput($"LocalIndexBuild: 경로가 없습니다: {inp.Path}", IsError: true);
            yield break;
        }

        if (files.Count == 0)
        {
            yield return new ToolOutput("LocalIndexBuild: 인덱싱할 지원 문서가 없습니다.", IsError: true);
            yield break;
        }

        var store = new ChunkStore(anchor);
        var manifest = store.LoadManifest();
        var size = inp.ChunkSize is { } cs and > 0 ? cs : manifest.ChunkSize;
        var overlap = inp.Overlap is { } ov and >= 0 ? ov : manifest.Overlap;
        var force = inp.Force == true || size != manifest.ChunkSize || overlap != manifest.Overlap;

        var bySource = manifest.Documents.ToDictionary(d => d.Source, StringComparer.Ordinal);
        var vm = store.LoadVectorManifest();
        var vectoredChunks = vm?.Documents.ToDictionary(d => d.Source, d => d.Chunks, StringComparer.Ordinal)
                             ?? new Dictionary<string, int>(StringComparer.Ordinal);

        string result;
        string? error = null;
        try
        {
            result = await BuildAsync(store, anchor, files, bySource, vectoredChunks, size, overlap, force, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
        }

        if (error is not null)
        {
            yield return new ToolOutput($"LocalIndexBuild: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static async System.Threading.Tasks.Task<string> BuildAsync(
        ChunkStore store, string anchor, List<string> files,
        Dictionary<string, ChunkDocEntry> bySource, Dictionary<string, int> vectoredChunks,
        int size, int overlap, bool force, CancellationToken ct)
    {
        var indexed = 0;
        var skipped = 0;
        var totalChunks = 0;
        string? model = null;
        var failures = new List<string>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(anchor, file);
            var fi = new FileInfo(file);
            var mtime = fi.LastWriteTimeUtc.Ticks;

            var textUnchanged = !force && bySource.TryGetValue(rel, out var prev)
                && prev.Size == fi.Length && prev.MTimeUtcTicks == mtime;
            var vectorsCurrent = textUnchanged && vectoredChunks.TryGetValue(rel, out var vc) && vc == bySource[rel].Chunks;

            if (textUnchanged && vectorsCurrent)
            {
                skipped++;
                totalChunks += bySource[rel].Chunks;
                continue;
            }

            IReadOnlyList<string> chunkTexts;
            if (textUnchanged)
            {
                // 텍스트는 그대로지만 벡터가 없거나 stale → 기존 청크를 임베딩만.
                chunkTexts = store.ReadChunks(bySource[rel].File).Select(c => c.Text).ToList();
            }
            else
            {
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
                chunkTexts = chunks;
            }

            if (chunkTexts.Count == 0)
            {
                continue;
            }

            var emb = await EmbeddingClient.EmbedAsync(chunkTexts, ct, model).ConfigureAwait(false);
            model ??= emb.Model;
            store.WriteVectors(rel, emb.Vectors, emb.Model, emb.Dim);
            indexed++;
            totalChunks += chunkTexts.Count;
        }

        store.SaveManifest(new ChunkManifest(size, overlap,
            bySource.Values.OrderBy(d => d.Source, StringComparer.Ordinal).ToList()));

        var sb = new StringBuilder();
        sb.Append("LocalIndexBuild 완료 — 인덱싱 ").Append(indexed).Append("개, 변경없음 ").Append(skipped)
          .Append("개, 총 청크 ").Append(totalChunks);
        if (model is not null)
        {
            sb.Append(" (model=").Append(model).Append(')');
        }

        sb.Append(". 로컬 저장: ").Append(store.ChunksDir);
        if (failures.Count > 0)
        {
            sb.Append(" | 실패 ").Append(failures.Count).Append(": ").Append(string.Join(", ", failures));
        }

        return sb.ToString();
    }
}
