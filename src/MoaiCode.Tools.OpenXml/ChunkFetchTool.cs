using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

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
            yield return new ToolOutput(L10n.Get("tools.chunkFetch.pathRequired"), IsError: true);
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
            yield return new ToolOutput(L10n.Get("tools.chunkFetch.badPath", pathError), IsError: true);
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
            yield return new ToolOutput(L10n.Get("tools.chunkFetch.pathNotFound", inp.Path), IsError: true);
            yield break;
        }

        var store = new ChunkStore(anchor);
        if (!store.Exists)
        {
            yield return new ToolOutput(
                L10n.Get("tools.chunkFetch.noChunks", anchor), IsError: true);
            yield break;
        }

        var manifest = store.LoadManifest();
        if (manifest.Documents.Count == 0)
        {
            yield return new ToolOutput(L10n.Get("tools.chunkFetch.noDocs"), IsError: true);
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
                L10n.Get("tools.chunkFetch.docNotFound", source), IsError: true);
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
        sb.AppendLine(L10n.Get("tools.chunkFetch.overviewHeader",
            manifest.Documents.Count, total, manifest.ChunkSize, manifest.Overlap));
        foreach (var d in manifest.Documents)
        {
            sb.AppendLine(L10n.Get("tools.chunkFetch.overviewDocLine", d.Source, d.Chunks));
        }

        sb.Append(L10n.Get("tools.chunkFetch.overviewFooter"));
        return sb.ToString();
    }

    private static string RenderChunks(ChunkDocEntry entry, int total, int offset, int limit, List<ChunkLine> page)
    {
        var sb = new StringBuilder();
        var end = offset + page.Count;
        sb.AppendLine(L10n.Get("tools.chunkFetch.chunksHeader",
            entry.Source, total, offset, end == 0 ? 0 : end - 1));
        foreach (var c in page)
        {
            sb.Append('[').Append(c.Index).Append("] ").AppendLine(c.Text);
        }

        if (end < total)
        {
            sb.Append(L10n.Get("tools.chunkFetch.chunksMore", end));
        }
        else if (page.Count == 0)
        {
            sb.Append(L10n.Get("tools.chunkFetch.chunksEmpty"));
        }
        else
        {
            sb.Append(L10n.Get("tools.chunkFetch.chunksEnd"));
        }

        return sb.ToString();
    }
}
