using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoaiCode.Localization;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// 대상 폴더 아래 <c>.moai-chunks/</c> 사이드카에 청크(jsonl)와 manifest 를 읽고 쓴다. 원본은 건드리지 않는다.
/// 문서별 파일은 상대경로를 미러링(<c>.moai-chunks/sub/doc.docx.jsonl</c>)해 충돌을 피한다.
/// </summary>
public sealed class ChunkStore
{
    public const string DirName = ".moai-chunks";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _chunksDir;

    public ChunkStore(string anchorDir)
    {
        _chunksDir = Path.Combine(anchorDir, DirName);
    }

    public string ChunksDir => _chunksDir;

    public bool Exists => Directory.Exists(_chunksDir);

    public string ManifestPath => Path.Combine(_chunksDir, "manifest.json");

    public ChunkManifest LoadManifest()
    {
        if (!File.Exists(ManifestPath))
        {
            return new ChunkManifest(TextChunker.DefaultMaxChars, TextChunker.DefaultOverlapChars, new());
        }

        try
        {
            return JsonSerializer.Deserialize<ChunkManifest>(File.ReadAllText(ManifestPath), Json)
                   ?? new ChunkManifest(TextChunker.DefaultMaxChars, TextChunker.DefaultOverlapChars, new());
        }
        catch (JsonException)
        {
            return new ChunkManifest(TextChunker.DefaultMaxChars, TextChunker.DefaultOverlapChars, new());
        }
    }

    public void SaveManifest(ChunkManifest manifest)
    {
        Directory.CreateDirectory(_chunksDir);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, Json));
    }

    // 문서(상대경로)의 청크를 jsonl 로 기록한다. 반환값은 manifest 에 저장할 상대 파일명.
    public string WriteChunks(string relSource, IReadOnlyList<string> chunks)
    {
        var relFile = relSource + ".jsonl";
        var full = Path.Combine(_chunksDir, relFile);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var sb = new StringBuilder();
        for (var i = 0; i < chunks.Count; i++)
        {
            sb.AppendLine(JsonSerializer.Serialize(new ChunkLine(i, chunks[i]), Json));
        }

        File.WriteAllText(full, sb.ToString());
        return relFile;
    }

    public string VectorManifestPath => Path.Combine(_chunksDir, "vectors.json");

    // 파이프라인이 만든 벡터 메타(vectors.json). 없으면 null.
    public VectorManifest? LoadVectorManifest()
    {
        if (!File.Exists(VectorManifestPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<VectorManifest>(File.ReadAllText(VectorManifestPath), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 문서의 벡터 파일(.vec)을 [청크수 × dim] float32 로 읽는다. 크기가 dim 배수가 아니면 예외.
    public IReadOnlyList<float[]> ReadVectors(string vecRelFile, int dim)
    {
        var full = Path.Combine(_chunksDir, vecRelFile);
        if (!File.Exists(full))
        {
            return Array.Empty<float[]>();
        }

        var bytes = File.ReadAllBytes(full);
        if (dim <= 0 || bytes.Length % (dim * 4) != 0)
        {
            throw new InvalidDataException(
                L10n.Get("tools.chunkStore.vecSizeMismatch", vecRelFile, bytes.Length, dim));
        }

        var rows = bytes.Length / (dim * 4);
        var result = new List<float[]>(rows);
        for (var r = 0; r < rows; r++)
        {
            var vec = new float[dim];
            Buffer.BlockCopy(bytes, r * dim * 4, vec, 0, dim * 4);
            result.Add(vec);
        }

        return result;
    }

    // 문서 벡터를 .vec(리틀엔디언 float32 행 우선) 로 쓰고 vectors.json 에 upsert 한다(로컬 임베딩).
    // relSource 는 manifest 의 source(상대경로, 예 "sub/plan.docx") — CHUNK_VEC_FORMAT.md 계약.
    public void WriteVectors(string relSource, IReadOnlyList<float[]> vectors, string embModel, int dim)
    {
        var vecRel = relSource + ".vec";
        var full = Path.Combine(_chunksDir, vecRel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var bytes = new byte[checked(vectors.Count * dim * 4)];
        for (var r = 0; r < vectors.Count; r++)
        {
            Buffer.BlockCopy(vectors[r], 0, bytes, r * dim * 4, dim * 4);
        }

        File.WriteAllBytes(full, bytes);

        var vm = LoadVectorManifest();
        var docs = vm?.Documents ?? new List<VectorDocEntry>();
        docs.RemoveAll(d => string.Equals(d.Source, relSource, StringComparison.Ordinal));
        docs.Add(new VectorDocEntry(relSource, vecRel, vectors.Count));
        File.WriteAllText(VectorManifestPath, JsonSerializer.Serialize(new VectorManifest(embModel, dim, docs), Json));
    }

    // 문서의 벡터를 vectors.json + .vec 에서 제거(원본 삭제 시).
    public void RemoveVectors(string relSource)
    {
        var vm = LoadVectorManifest();
        if (vm is null)
        {
            return;
        }

        var entry = vm.Documents.FirstOrDefault(d => string.Equals(d.Source, relSource, StringComparison.Ordinal));
        vm.Documents.RemoveAll(d => string.Equals(d.Source, relSource, StringComparison.Ordinal));
        File.WriteAllText(VectorManifestPath, JsonSerializer.Serialize(vm, Json));
        if (entry is not null)
        {
            try
            {
                var full = Path.Combine(_chunksDir, entry.Vec);
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch
            {
                // 벡터 파일 삭제 실패는 치명적 아님.
            }
        }
    }

    public IReadOnlyList<ChunkLine> ReadChunks(string relFile)
    {
        var full = Path.Combine(_chunksDir, relFile);
        if (!File.Exists(full))
        {
            return Array.Empty<ChunkLine>();
        }

        var result = new List<ChunkLine>();
        foreach (var line in File.ReadLines(full))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var c = JsonSerializer.Deserialize<ChunkLine>(line, Json);
                if (c is not null)
                {
                    result.Add(c);
                }
            }
            catch (JsonException)
            {
                // skip corrupt line
            }
        }

        return result;
    }
}

public sealed record ChunkLine(
    [property: JsonPropertyName("i")] int Index,
    [property: JsonPropertyName("text")] string Text);

public sealed record ChunkDocEntry(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mtime")] long MTimeUtcTicks,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("file")] string File);

public sealed record ChunkManifest(
    [property: JsonPropertyName("chunkSize")] int ChunkSize,
    [property: JsonPropertyName("overlap")] int Overlap,
    [property: JsonPropertyName("documents")] List<ChunkDocEntry> Documents);

// 외부 임베딩 파이프라인이 소유하는 vectors.json (moai 는 읽기만). 스펙: docs/CHUNK_VEC_FORMAT.md.
public sealed record VectorDocEntry(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("vec")] string Vec,
    [property: JsonPropertyName("chunks")] int Chunks);

public sealed record VectorManifest(
    [property: JsonPropertyName("embModel")] string? EmbModel,
    [property: JsonPropertyName("dim")] int Dim,
    [property: JsonPropertyName("documents")] List<VectorDocEntry> Documents);
