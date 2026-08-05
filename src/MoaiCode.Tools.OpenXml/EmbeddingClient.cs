using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MoaiCode.Tools.OpenXml;

/// <summary>
/// open-moai 서버의 compute-only 임베딩 엔드포인트(POST {baseUrl}/embeddings, OpenAI 호환)를 호출한다.
/// 서버는 텍스트를 임베딩만 하고 저장하지 않는다 — 로컬 청킹 파이프라인이 이 벡터로 .vec/vectors.json 을
/// 만든다(문서 원본은 서버로 안 감). 질의 임베딩도 같은 엔드포인트/모델을 써야 저장 벡터와 비교가 유효.
/// </summary>
public static class EmbeddingClient
{
    private const int MaxBatch = 512; // 서버 상한

    public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, string Model, int Dim);

    private sealed record Req(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("model")] string? Model);

    private sealed record RespItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);

    private sealed record Resp(
        [property: JsonPropertyName("data")] List<RespItem>? Data,
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("dimensions")] int? Dimensions);

    /// <summary>텍스트들을 임베딩한다(입력 순서 유지). model 미지정 시 서버 기본(text-embedding-3-small).
    /// 자격증명/서버주소가 없으면 예외. 512개씩 배치 호출.</summary>
    public static async Task<EmbeddingResult> EmbedAsync(
        IReadOnlyList<string> inputs, CancellationToken ct, string? model = null)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("open-moai connection info missing (login required).");
        }

        var url = baseUrl.TrimEnd('/') + "/embeddings";

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

        var all = new List<float[]>(inputs.Count);
        string? outModel = model;
        var dim = 0;

        for (var start = 0; start < inputs.Count; start += MaxBatch)
        {
            var batch = new List<string>();
            for (var i = start; i < inputs.Count && i < start + MaxBatch; i++)
            {
                batch.Add(inputs[i]);
            }

            using var resp = await client.PostAsJsonAsync(url, new Req(batch, model), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"embeddings HTTP {(int)resp.StatusCode}: {body.Trim()}");
            }

            var data = await resp.Content.ReadFromJsonAsync<Resp>(cancellationToken: ct).ConfigureAwait(false);
            if (data?.Data is null || data.Data.Count != batch.Count)
            {
                throw new InvalidOperationException("embeddings response malformed (count mismatch).");
            }

            outModel ??= data.Model;
            data.Data.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var item in data.Data)
            {
                if (dim == 0)
                {
                    dim = item.Embedding.Length;
                }

                all.Add(item.Embedding);
            }
        }

        if (dim == 0)
        {
            throw new InvalidOperationException("embeddings returned no vectors.");
        }

        return new EmbeddingResult(all, outModel ?? "unknown", dim);
    }
}
