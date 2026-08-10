using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Localization;
using MoaiCode.Tools.OpenXml;

namespace MoaiCode.Gui.Agent;

/// <summary>조직 문서함 RAG 검색 결과 한 건(관련 청크).</summary>
public sealed record OrgHit(string Title, string Snippet, string DocumentId, double Score);

/// <summary>첨부 확정된 참조(스니펫 또는 원문 통째).</summary>
public sealed record PickedRef(string Title, string Text, string DocumentId, bool WholeDoc);

/// <summary>
/// 조직 문서함 접근(모드 B). Bearer API 키로 검색(/v1/knowledge/search) 및
/// 원문 다운로드(/v1/knowledge/{id}?download=1) → 텍스트 추출.
/// </summary>
public static class OrgSearchClient
{
    public static async Task<(IReadOnlyList<OrgHit> Hits, string? Error)> SearchAsync(
        string query, CancellationToken ct)
    {
        var (baseUrl, key, envErr) = Env();
        if (envErr is not null)
        {
            return (Array.Empty<OrgHit>(), envErr);
        }

        using var client = NewClient(key!);
        var b = baseUrl!.TrimEnd('/');

        var (orgId, orgErr) = await ResolveOrgIdAsync(client, b, ct).ConfigureAwait(false);
        if (orgErr is not null)
        {
            return (Array.Empty<OrgHit>(), orgErr);
        }

        try
        {
            using var resp = await client.PostAsJsonAsync(
                b + "/knowledge/search", new { orgId, query, topK = 8 }, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (Array.Empty<OrgHit>(), L10n.Get("gui.org.errSearchHttpFmt", (int)resp.StatusCode, body.Trim()));
            }

            var data = await resp.Content.ReadFromJsonAsync<SearchResp>(cancellationToken: ct).ConfigureAwait(false);
            var hits = (data?.Results ?? new List<Hit>())
                .Select(r => new OrgHit(
                    string.IsNullOrWhiteSpace(r.Title) ? L10n.Get("gui.org.noTitle") : r.Title!,
                    r.Snippet ?? string.Empty,
                    r.DocumentId.ValueKind == JsonValueKind.Undefined ? string.Empty : r.DocumentId.ToString(),
                    r.Score ?? 0))
                .Where(h => !string.IsNullOrWhiteSpace(h.Snippet))
                .ToList();
            return (hits, null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<OrgHit>(), L10n.Get("gui.org.errSearchFmt", ex.Message));
        }
    }

    /// <summary>문서 원문을 다운로드해 텍스트로 추출한다(원문 통째 첨부용).</summary>
    public static async Task<(string? Text, string? Error)> DownloadDocTextAsync(
        string documentId, CancellationToken ct)
    {
        var (baseUrl, key, envErr) = Env();
        if (envErr is not null)
        {
            return (null, envErr);
        }

        using var client = NewClient(key!);
        var b = baseUrl!.TrimEnd('/');

        var (orgId, orgErr) = await ResolveOrgIdAsync(client, b, ct).ConfigureAwait(false);
        if (orgErr is not null)
        {
            return (null, orgErr);
        }

        var url = $"{b}/knowledge/{Uri.EscapeDataString(documentId)}?download=1&orgId={Uri.EscapeDataString(orgId!)}";
        string tmp;
        try
        {
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (null, L10n.Get("gui.org.errDownloadHttpFmt", (int)resp.StatusCode, body.Trim()));
            }

            var fileName = resp.Content.Headers.ContentDisposition?.FileNameStar
                           ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                           ?? "doc.bin";
            if (!DocumentTextExtractor.IsSupported(fileName))
            {
                return (null, L10n.Get("gui.org.errUnsupportedFmt", Path.GetExtension(fileName)));
            }

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            tmp = Path.Combine(Path.GetTempPath(), $"moai_org_{documentId}{Path.GetExtension(fileName)}");
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, L10n.Get("gui.org.errDownloadFmt", ex.Message));
        }

        try
        {
            return (DocumentTextExtractor.Extract(tmp), null);
        }
        catch (Exception ex)
        {
            return (null, L10n.Get("gui.org.errExtractFmt", ex.Message));
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // 임시파일 정리 실패는 무시
            }
        }
    }

    private static (string? BaseUrl, string? Key, string? Error) Env()
    {
        var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key)
            ? (null, null, L10n.Get("gui.org.errLogin"))
            : (baseUrl, key, null);
    }

    private static HttpClient NewClient(string key)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private static async Task<(string? OrgId, string? Error)> ResolveOrgIdAsync(
        HttpClient client, string b, CancellationToken ct)
    {
        try
        {
            var orgs = await client.GetFromJsonAsync<OrgListResp>(b + "/organizations", ct).ConfigureAwait(false);
            var id = (orgs?.Items ?? orgs?.Organizations)?.FirstOrDefault()?.Id;
            return string.IsNullOrEmpty(id) ? (null, L10n.Get("gui.org.errNoOrg")) : (id, null);
        }
        catch (Exception ex)
        {
            return (null, L10n.Get("gui.org.errOrgLookupFmt", ex.Message));
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
