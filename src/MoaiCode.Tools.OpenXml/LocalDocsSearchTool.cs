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
/// 로컬 인덱스(.moai-chunks/*.vec + vectors.json)에 대해 의미 검색을 한다. 질의를 서버 임베딩 엔드포인트로
/// 임베딩(원본은 서버에 안 감)한 뒤 오프라인 코사인 top-K. LocalIndexBuild 로 먼저 인덱싱해야 한다.
/// 조직 문서함(OrgDocsSearch)과 달리 이 문서들은 서버에 저장/공유되지 않은 '로컬 전용' 이다.
/// </summary>
public sealed class LocalDocsSearchTool : ITool
{
    public string Name => "LocalDocsSearch";

    public string Description => """
        Semantic search over LOCAL documents indexed with LocalIndexBuild (private, never uploaded/shared).
        Provide `query` (natural language) and `path` (a folder that has been indexed). The query is embedded
        via the server's compute-only endpoint and matched against locally stored vectors — fully offline
        vector math. Returns the top-K most relevant chunks (source file, chunk index, score, text). If the
        folder has no local index yet, run LocalIndexBuild first.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "Natural-language search query" },
            "path": { "type": "string", "description": "Indexed folder to search" },
            "topK": { "type": "integer", "description": "Number of results (default 5)" }
          },
          "required": ["query", "path"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("query")] string? Query,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("topK")] int? TopK);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Query) || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("LocalDocsSearch: 'query' 와 'path' 가 필요합니다.", IsError: true);
            yield break;
        }

        var anchor = string.Empty;
        string? pathError = null;
        try
        {
            anchor = Path.GetFullPath(Path.IsPathRooted(inp.Path) ? inp.Path : Path.Combine(context.WorkingDirectory, inp.Path));
        }
        catch (Exception ex)
        {
            pathError = ex.Message;
        }

        if (pathError is not null)
        {
            yield return new ToolOutput($"LocalDocsSearch: 잘못된 경로 — {pathError}", IsError: true);
            yield break;
        }

        var store = new ChunkStore(anchor);
        var vm = store.Exists ? store.LoadVectorManifest() : null;
        if (vm is null || vm.Documents.Count == 0)
        {
            yield return new ToolOutput("LocalDocsSearch: 이 폴더에 로컬 인덱스가 없습니다. 먼저 LocalIndexBuild 로 인덱싱하세요.", IsError: true);
            yield break;
        }

        var topK = inp.TopK is { } k and > 0 ? k : 5;
        string result;
        string? error = null;
        try
        {
            result = await SearchAsync(store, vm, inp.Query!, topK, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            result = string.Empty;
        }

        if (error is not null)
        {
            yield return new ToolOutput($"LocalDocsSearch: 실패 — {error}", IsError: true);
            yield break;
        }

        yield return new ToolOutput(result);
    }

    private static async System.Threading.Tasks.Task<string> SearchAsync(
        ChunkStore store, VectorManifest vm, string query, int topK, CancellationToken ct)
    {
        var top = await LocalSearch.SearchAsync(store, vm, query, topK, ct).ConfigureAwait(false);
        if (top.Count == 0)
        {
            return "LocalDocsSearch: 일치하는 청크가 없습니다.";
        }

        var sb = new StringBuilder();
        sb.Append("로컬 검색 결과 top-").Append(top.Count).Append(" (model=").Append(vm.EmbModel).Append("):\n");
        foreach (var h in top)
        {
            var snippet = h.Text.Length > 400 ? h.Text[..400] + "…" : h.Text;
            sb.Append("\n• [").Append(h.Source).Append(" #").Append(h.Index).Append("] score=")
              .Append(h.Score.ToString("0.000")).Append('\n').Append(snippet).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>로컬 인덱스(.moai-chunks) 코사인 검색의 구조적 결과(툴·GUI 검색창 공용).</summary>
public sealed record LocalHit(string Source, int Index, float Score, string Text);

/// <summary>로컬 벡터 검색 코어 — 질의를 서버 임베딩(compute-only)으로 벡터화한 뒤 오프라인 코사인 top-K.</summary>
public static class LocalSearch
{
    /// <summary>폴더(anchor)의 로컬 인덱스에서 top-K 검색. 인덱스가 없으면 빈 목록.</summary>
    public static async System.Threading.Tasks.Task<IReadOnlyList<LocalHit>> SearchAsync(
        string anchor, string query, int topK, CancellationToken ct)
    {
        var store = new ChunkStore(anchor);
        var vm = store.Exists ? store.LoadVectorManifest() : null;
        if (vm is null || vm.Documents.Count == 0)
        {
            return Array.Empty<LocalHit>();
        }

        return await SearchAsync(store, vm, query, topK, ct).ConfigureAwait(false);
    }

    public static async System.Threading.Tasks.Task<IReadOnlyList<LocalHit>> SearchAsync(
        ChunkStore store, VectorManifest vm, string query, int topK, CancellationToken ct)
    {
        var emb = await EmbeddingClient.EmbedAsync(new[] { query }, ct, vm.EmbModel).ConfigureAwait(false);
        var q = emb.Vectors[0];
        if (q.Length != vm.Dim)
        {
            throw new InvalidDataException($"질의 차원({q.Length})이 인덱스 dim({vm.Dim})과 다릅니다. 재인덱싱이 필요합니다.");
        }

        var hits = new List<LocalHit>();
        foreach (var doc in vm.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var chunks = store.ReadChunks(doc.Source + ".jsonl");
            if (chunks.Count != doc.Chunks)
            {
                continue; // stale: 청크 수 불일치 → 건너뜀
            }

            IReadOnlyList<float[]> vecs;
            try
            {
                vecs = store.ReadVectors(doc.Vec, vm.Dim);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            var n = Math.Min(vecs.Count, chunks.Count);
            for (var i = 0; i < n; i++)
            {
                hits.Add(new LocalHit(doc.Source, chunks[i].Index, Cosine(q, vecs[i]), chunks[i].Text));
            }
        }

        return hits.OrderByDescending(h => h.Score).Take(topK).ToList();
    }

    private static float Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0f : (float)(dot / (Math.Sqrt(na) * Math.Sqrt(nb)));
    }
}
