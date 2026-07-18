using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// ChunkBuild 로 만든 로컬 청크(<c>.moai-chunks/</c>)를 가져온다(읽기 전용). 문서/폴더의 청크를 순서대로
/// 반환(랭킹 없음 — 소비자가 판단). 대량 방지를 위해 offset/limit 로 페이지네이션한다.
/// </summary>
public sealed class ChunkFetchTool : ITool
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 1000;

    public string Name => "ChunkFetch";

    public string Description => """
        Fetches locally pre-chunked document text produced by ChunkBuild (from `.moai-chunks/`). Returns
        chunks in order (no ranking — the caller decides relevance). Point `path` at a directory to see an
        overview (documents + chunk counts), or at a specific file (or pass `source`) to get that
        document's chunks. Use `offset`/`limit` to page through large sets. Read-only, offline,
        server-independent. For semantic organization search use OrgDocs instead.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory (overview) or a specific file to fetch chunks for (relative to workspace)" },
            "source": { "type": "string", "description": "Specific document (relative to the directory) to fetch, when path is a directory" },
            "offset": { "type": "integer", "description": "First chunk index to return (default 0)" },
            "limit": { "type": "integer", "description": "Max chunks to return (default 25)" }
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("offset")] int? Offset,
        [property: JsonPropertyName("limit")] int? Limit);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("ChunkFetch: 'path'(디렉토리 또는 파일)가 필요합니다.", IsError: true);
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
            yield return new ToolOutput($"ChunkFetch: 잘못된 경로 — {pathError}", IsError: true);
            yield break;
        }

        // 앵커 + 대상 문서 결정.
        string anchor;
        string? source = inp.Source;
        if (Directory.Exists(full))
        {
            anchor = full;
        }
        else if (File.Exists(full))
        {
            anchor = System.IO.Path.GetDirectoryName(full)!;
            source = System.IO.Path.GetFileName(full);
        }
        else
        {
            yield return new ToolOutput($"ChunkFetch: 경로가 없습니다: {inp.Path}", IsError: true);
            yield break;
        }

        var store = new ChunkStore(anchor);
        if (!store.Exists)
        {
            yield return new ToolOutput(
                $"ChunkFetch: '{anchor}' 에 청크가 없습니다. 먼저 ChunkBuild 로 청킹하세요.", IsError: true);
            yield break;
        }

        var manifest = store.LoadManifest();
        if (manifest.Documents.Count == 0)
        {
            yield return new ToolOutput("ChunkFetch: 청킹된 문서가 없습니다. ChunkBuild 를 먼저 실행하세요.", IsError: true);
            yield break;
        }

        // source 미지정 + 디렉토리 → 개요.
        if (string.IsNullOrWhiteSpace(source))
        {
            yield return new ToolOutput(RenderOverview(manifest));
            yield break;
        }

        var entry = manifest.Documents.FirstOrDefault(d =>
            string.Equals(d.Source, source, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            yield return new ToolOutput(
                $"ChunkFetch: '{source}' 문서를 찾을 수 없습니다. 개요를 보려면 path 를 디렉토리로 주세요.", IsError: true);
            yield break;
        }

        var chunks = store.ReadChunks(entry.File);
        var offset = Math.Max(0, inp.Offset ?? 0);
        var limit = Math.Clamp(inp.Limit ?? DefaultLimit, 1, MaxLimit);
        var page = chunks.Skip(offset).Take(limit).ToList();

        yield return new ToolOutput(Reminders.UntrustedToolOutput + RenderChunks(entry, chunks.Count, offset, limit, page));
    }

    private static string RenderOverview(ChunkManifest manifest)
    {
        var total = manifest.Documents.Sum(d => d.Chunks);
        var sb = new StringBuilder();
        sb.Append(".moai-chunks 개요 — 문서 ").Append(manifest.Documents.Count).Append("개, 총 청크 ")
          .Append(total).Append("개 (청크 ").Append(manifest.ChunkSize).Append("자·오버랩 ")
          .Append(manifest.Overlap).AppendLine("자)");
        foreach (var d in manifest.Documents)
        {
            sb.Append("  • ").Append(d.Source).Append(" — ").Append(d.Chunks).Append("청크").AppendLine();
        }

        sb.Append("→ 특정 문서 청크: source 를 위 이름으로 지정(또는 path 를 그 파일로) + offset/limit.");
        return sb.ToString();
    }

    private static string RenderChunks(ChunkDocEntry entry, int total, int offset, int limit, List<ChunkLine> page)
    {
        var sb = new StringBuilder();
        var end = offset + page.Count;
        sb.Append("문서: ").Append(entry.Source).Append(" — 총 ").Append(total).Append("청크 중 ")
          .Append(offset).Append('–').Append(end == 0 ? 0 : end - 1).AppendLine(" 표시");
        foreach (var c in page)
        {
            sb.Append('[').Append(c.Index).Append("] ").AppendLine(c.Text);
        }

        if (end < total)
        {
            sb.Append("(더 있음: offset=").Append(end).Append(" 으로 이어서 조회)");
        }
        else if (page.Count == 0)
        {
            sb.Append("(해당 범위에 청크 없음 — offset 확인)");
        }
        else
        {
            sb.Append("(끝)");
        }

        return sb.ToString();
    }
}
