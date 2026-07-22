using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MoaiCode.Gui.Agent;

/// <summary>조직 문서함 RAG 검색 결과 한 건(관련 청크).</summary>
public sealed record OrgHit(string Title, string Snippet, string DocumentId, double Score);

/// <summary>
/// 조직 문서함 의미검색(모드 B 조직 참조용). Bearer API 키로 /v1/knowledge/search 호출.
/// 원문 다운로드는 웹세션 전용이라 불가 → RAG 스니펫을 참조로 가져온다.
/// </summary>
public static class OrgSearchClient
{
    public static async Task<(IReadOnlyList<OrgHit> Hits, string? Error)> SearchAsync(
        string query, CancellationToken ct)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            return (Array.Empty<OrgHit>(), "로그인이 필요합니다. `moai login` 후 이용하세요.");
        }

        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        var b = baseUrl.TrimEnd('/');

        string? orgId;
        try
        {
            var orgs = await client.GetFromJsonAsync<OrgListResp>(b + "/organizations", ct).ConfigureAwait(false);
            orgId = (orgs?.Items ?? orgs?.Organizations)?.FirstOrDefault()?.Id;
        }
        catch (Exception ex)
        {
            return (Array.Empty<OrgHit>(), "조직 조회 실패: " + ex.Message);
        }

        if (string.IsNullOrEmpty(orgId))
        {
            return (Array.Empty<OrgHit>(), "소속 조직을 찾지 못했습니다.");
        }

        try
        {
            using var resp = await client.PostAsJsonAsync(
                b + "/knowledge/search", new { orgId, query, topK = 8 }, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (Array.Empty<OrgHit>(), $"검색 실패 HTTP {(int)resp.StatusCode}: {body.Trim()}");
            }

            var data = await resp.Content.ReadFromJsonAsync<SearchResp>(cancellationToken: ct).ConfigureAwait(false);
            var hits = (data?.Results ?? new List<Hit>())
                .Select(r => new OrgHit(
                    string.IsNullOrWhiteSpace(r.Title) ? "(제목 없음)" : r.Title!,
                    r.Snippet ?? string.Empty,
                    r.DocumentId.ValueKind == JsonValueKind.Undefined ? string.Empty : r.DocumentId.ToString(),
                    r.Score ?? 0))
                .Where(h => !string.IsNullOrWhiteSpace(h.Snippet))
                .ToList();
            return (hits, null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<OrgHit>(), "검색 오류: " + ex.Message);
        }
    }

    private sealed record OrgListResp(
        [property: JsonPropertyName("items")] List<Org>? Items,
        [property: JsonPropertyName("organizations")] List<Org>? Organizations);

    private sealed record Org([property: JsonPropertyName("id")] string? Id);

    private sealed record SearchResp([property: JsonPropertyName("results")] List<Hit>? Results);

    private sealed record Hit(
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("snippet")] string? Snippet,
        [property: JsonPropertyName("documentId")] JsonElement DocumentId,
        [property: JsonPropertyName("score")] double? Score);
}
