using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Core.Agent.Prompts;
using MoaiCode.Core.Tools;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 외부 파이프라인이 만든 로컬 벡터(<c>.moai-chunks/*.vec</c> + <c>vectors.json</c>)에 대해 코사인 top-K 를
/// 오프라인으로 계산한다(읽기 전용). 질의 벡터는 호출자가 같은 임베딩 모델로 미리 계산해 넘긴다(CLI 는
/// 임베딩하지 않는다). 스펙: docs/CHUNK_VEC_FORMAT.md.
/// </summary>
public sealed class ChunkSearchTool : ITool
{
    private const int DefaultTopK = 5;
    private const int MaxTopK = 100;

    public string Name => "ChunkSearch";

    public string Description => """
        Cosine top-K search over locally stored chunk vectors (`.moai-chunks/*.vec` + `vectors.json`,
        produced by an external embedding pipeline — see docs/CHUNK_VEC_FORMAT.md). Fully offline: the CLI
        only does the vector math. You must supply a `queryVector` (array of floats) or `queryVectorFile`
        (path) precomputed with the SAME embedding model as the stored vectors — the CLI does not embed
        text. Returns the top-K most similar chunks (source, index, score, text). If no vectors.json exists,
        the external pipeline hasn't embedded yet. For local text chunks without vectors use ChunkFetch.
        """;

    public bool IsReadOnly => true;
    public bool IsConcurrencySafe => true;

    public JsonElement InputSchema { get; } = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Directory whose .moai-chunks/ holds the vectors" },
            "queryVector": { "type": "array", "items": { "type": "number" }, "description": "Query embedding (same model/dim as stored vectors)" },
            "queryVectorFile": { "type": "string", "description": "Path to the query vector (.vec single row, or JSON array)" },
            "topK": { "type": "integer", "description": "Number of results (default 5)" },
            "source": { "type": "string", "description": "Restrict to one document (relative path), optional" }
          },
          "required": ["path"]
        }
        """).RootElement.Clone();

    private sealed record Input(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("queryVector")] List<double>? QueryVector,
        [property: JsonPropertyName("queryVectorFile")] string? QueryVectorFile,
        [property: JsonPropertyName("topK")] int? TopK,
        [property: JsonPropertyName("source")] string? Source);

    public async IAsyncEnumerable<ToolProgress> ExecuteAsync(
        JsonElement input, ToolContext context, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        var inp = input.Deserialize<Input>();
        if (inp is null || string.IsNullOrWhiteSpace(inp.Path))
        {
            yield return new ToolOutput("ChunkSearch: 'path'(디렉토리)가 필요합니다.", IsError: true);
            yield break;
        }

        var anchor = "";
        string? setupError = null;
        try
        {
            anchor = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(inp.Path)
                ? inp.Path
                : System.IO.Path.Combine(context.WorkingDirectory, inp.Path));
        }
        catch (Exception ex)
        {
            setupError = $"ChunkSearch: 잘못된 경로 — {ex.Message}";
        }

        if (setupError is null && !Directory.Exists(anchor))
        {
            setupError = $"ChunkSearch: 디렉토리가 없습니다: {inp.Path}";
        }

        if (setupError is not null)
        {
            yield return new ToolOutput(setupError, IsError: true);
            yield break;
        }

        var store = new ChunkStore(anchor);
        var vm = store.Exists ? store.LoadVectorManifest() : null;
        if (vm is null || vm.Documents.Count == 0)
        {
            yield return new ToolOutput(
                "ChunkSearch: 벡터(.moai-chunks/vectors.json)가 없습니다. 외부 임베딩 파이프라인이 " +
                "먼저 벡터를 생성해야 합니다 (docs/CHUNK_VEC_FORMAT.md).", IsError: true);
            yield break;
        }

        float[] query;
        var queryError = LoadQuery(inp, context, vm.Dim, out query);
        if (queryError is not null)
        {
            yield return new ToolOutput(queryError, IsError: true);
            yield break;
        }

        var chunkManifest = store.LoadManifest();
        var chunkFileBySource = chunkManifest.Documents.ToDictionary(d => d.Source, d => d.File, StringComparer.Ordinal);

        var topK = Math.Clamp(inp.TopK ?? DefaultTopK, 1, MaxTopK);
        var results = new List<(double Score, string Source, int Index, string Text)>();
        var stale = 0;
        var failed = new List<string>();

        foreach (var doc in vm.Documents)
        {
            if (!string.IsNullOrWhiteSpace(inp.Source)
                && !string.Equals(doc.Source, inp.Source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var chunkFile = chunkFileBySource.TryGetValue(doc.Source, out var cf) ? cf : doc.Source + ".jsonl";
            var texts = store.ReadChunks(chunkFile);

            // stale: 벡터 개수와 현재 청크 개수가 어긋나면 이 문서는 건너뛴다(재임베딩 필요).
            if (texts.Count != doc.Chunks)
            {
                stale++;
                continue;
            }

            IReadOnlyList<float[]> vecs;
            try
            {
                vecs = store.ReadVectors(doc.Vec, vm.Dim);
            }
            catch (Exception ex)
            {
                failed.Add($"{doc.Source} ({ex.GetType().Name})");
                continue;
            }

            if (vecs.Count != texts.Count)
            {
                stale++;
                continue;
            }

            for (var i = 0; i < vecs.Count; i++)
            {
                var score = Cosine(query, vecs[i]);
                var text = i < texts.Count ? texts[i].Text : "";
                results.Add((score, doc.Source, i, text));
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = results.Take(topK).ToList();

        yield return new ToolOutput(Reminders.UntrustedToolOutput + Render(vm, top, results.Count, stale, failed));
    }

    private static string? LoadQuery(Input inp, ToolContext context, int dim, out float[] query)
    {
        query = Array.Empty<float>();

        if (inp.QueryVector is { Count: > 0 } qv)
        {
            query = qv.Select(x => (float)x).ToArray();
        }
        else if (!string.IsNullOrWhiteSpace(inp.QueryVectorFile))
        {
            string full;
            try
            {
                full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(inp.QueryVectorFile)
                    ? inp.QueryVectorFile
                    : System.IO.Path.Combine(context.WorkingDirectory, inp.QueryVectorFile));
            }
            catch (Exception ex)
            {
                return $"ChunkSearch: 잘못된 queryVectorFile — {ex.Message}";
            }

            if (!File.Exists(full))
            {
                return $"ChunkSearch: queryVectorFile 이 없습니다: {inp.QueryVectorFile}";
            }

            try
            {
                query = full.EndsWith(".vec", StringComparison.OrdinalIgnoreCase)
                    ? ReadFloat32(full)
                    : (JsonSerializer.Deserialize<List<double>>(File.ReadAllText(full)) ?? new())
                        .Select(x => (float)x).ToArray();
            }
            catch (Exception ex)
            {
                return $"ChunkSearch: queryVectorFile 파싱 실패 — {ex.Message}";
            }
        }
        else
        {
            return "ChunkSearch: 'queryVector' 또는 'queryVectorFile' 이 필요합니다 " +
                   "(외부에서 같은 임베딩 모델로 미리 계산한 질의 벡터).";
        }

        if (query.Length != dim)
        {
            return $"ChunkSearch: 질의 벡터 차원({query.Length})이 저장 벡터 dim({dim})과 다릅니다.";
        }

        return null;
    }

    private static float[] ReadFloat32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
        return floats;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na == 0 || nb == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static string Render(
        VectorManifest vm, List<(double Score, string Source, int Index, string Text)> top,
        int scanned, int stale, List<string> failed)
    {
        var sb = new StringBuilder();
        sb.Append("ChunkSearch — ").Append(top.Count).Append("건 (스캔 ").Append(scanned)
          .Append("청크, 모델 ").Append(vm.EmbModel ?? "?").Append(", dim ").Append(vm.Dim).Append(')');
        if (stale > 0)
        {
            sb.Append(" · stale 문서 ").Append(stale).Append("개 건너뜀");
        }

        sb.AppendLine();
        var rank = 1;
        foreach (var r in top)
        {
            sb.Append(rank++).Append(". [").Append(r.Score.ToString("0.000")).Append("] ")
              .Append(r.Source).Append(" #").Append(r.Index).AppendLine();
            sb.Append("   ").AppendLine(Truncate(r.Text, 300));
        }

        if (failed.Count > 0)
        {
            sb.Append("실패: ").Append(string.Join(", ", failed));
        }

        return sb.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
