using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MoaiCode.Localization;

namespace MoaiCode.Tools.Knowledge;

/// <summary>
/// 인증된 사용자(API 키)의 소속 조직을 서버에서 조회하고, 문서함 툴의 orgId 를 자동 해소한다.
/// 계약: GET {baseUrl}/organizations → { organizations: [{id,name,role,isPrimary}], count }.
/// 권한은 서버가 판정한다(본인 소속 조직만 반환 + 문서 API 는 멤버십 재검증) — 여기선 편의 해소만.
/// </summary>
internal static class OrgResolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    internal sealed record OrgInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("isPrimary")] bool? IsPrimary);

    private sealed record OrgsResponse(
        [property: JsonPropertyName("organizations")] List<OrgInfo>? Organizations,
        [property: JsonPropertyName("count")] int? Count);

    /// <summary>소속 조직 목록. 엔드포인트 없음/실패는 예외로 던진다.</summary>
    internal static async Task<IReadOnlyList<OrgInfo>> FetchAsync(
        string baseUrl, string key, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Timeout);

        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        var url = baseUrl.TrimEnd('/') + "/organizations";
        using var resp = await client.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointMissingException();
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        var data = await resp.Content.ReadFromJsonAsync<OrgsResponse>(cancellationToken: timeoutCts.Token)
            .ConfigureAwait(false);
        return (data?.Organizations ?? new List<OrgInfo>())
            .Where(o => !string.IsNullOrWhiteSpace(o.Id))
            .ToList();
    }

    /// <summary>
    /// orgId 를 해소한다. provided 가 있으면 그대로 사용(서버가 멤버십 검증). 없으면 소속 조직을 조회해
    /// 정확히 1개면 자동 사용, 0개/2개↑ 면 사용자에게 지정을 요구하는 안내 메시지를 Error 로 돌려준다.
    /// </summary>
    internal static async Task<(string? OrgId, string? Error)> ResolveAsync(
        string toolName, string baseUrl, string key, string? provided, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            return (provided.Trim(), null);
        }

        IReadOnlyList<OrgInfo> orgs;
        try
        {
            orgs = await FetchAsync(baseUrl, key, ct).ConfigureAwait(false);
        }
        catch (EndpointMissingException)
        {
            return (null, L10n.Get("tools.orgResolver.endpointMissing", toolName));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, L10n.Get("tools.orgResolver.timeout", toolName));
        }
        catch (HttpRequestException ex)
        {
            return (null, L10n.Get("tools.orgResolver.requestFailed", toolName, ex.Message));
        }

        if (orgs.Count == 0)
        {
            return (null, L10n.Get("tools.orgResolver.noOrgs", toolName));
        }

        if (orgs.Count == 1)
        {
            return (orgs[0].Id, null);
        }

        var list = string.Join(", ", orgs.Select(o =>
            $"[{o.Id}] {o.Name}{(o.IsPrimary == true ? L10n.Get("tools.orgResolver.primary") : "")}"));
        return (null, L10n.Get("tools.orgResolver.ambiguous", toolName, list));
    }

    internal sealed class EndpointMissingException : Exception
    {
    }
}
