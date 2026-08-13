using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MoaiCode.Localization;

namespace MoaiCode.Config;

/// <summary>CLI/GUI 로그인 응답 (open-moai /api/cli/login[/mfa]).</summary>
public sealed record LoginResult(
    string Status,
    string? ApiKey,
    string? BaseUrl,
    string? DefaultModel,
    IReadOnlyList<string> Models,
    string? MfaToken,
    string? Error,
    string? OrgName,
    string? Name = null);

/// <summary>팀 공유 스킬 1건 (open-moai GET {baseUrl}/skills 의 items[] 요소).</summary>
public sealed record TeamSkillItem(string Name, string Description, string Body);

/// <summary>open-moai 사이트의 CLI 로그인 엔드포인트 클라이언트. (CLI·GUI 공유)</summary>
public sealed class OpenMoaiClient
{
    private readonly HttpClient _http;
    private readonly string _host;
    private readonly string _client;

    // clientKind: 로그인 주체 식별자("cli" | "desktop"). 서버가 이 값으로 CLI/Desktop 키를 분리 발급한다.
    public OpenMoaiClient(string host, HttpClient? http = null, string clientKind = "cli")
    {
        _host = host.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _client = string.IsNullOrWhiteSpace(clientKind) ? "cli" : clientKind;
    }

    public Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
        => PostAsync("/api/cli/login", new { email, password, client = _client }, ct);

    public Task<LoginResult> LoginMfaAsync(string mfaToken, string code, CancellationToken ct)
        => PostAsync("/api/cli/login/mfa", new { mfaToken, code, client = _client }, ct);

    // ── 팀 공유 스킬 (baseUrl = host + "/api/v1"; Knowledge 툴과 동일한 Bearer GET 패턴) ──────────
    // login 엔드포인트(_host 기반)와 달리 baseUrl 을 그대로 받는 static 헬퍼 — 실패는 예외로 던지고,
    // 호출측(TeamSkills)이 non-fatal 로 감싼다. 짧은 타임아웃(5s)으로 시작 지연을 막는다.
    private static readonly TimeSpan SkillsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>호출자 소속 조직의 primary(없으면 first) orgId. 없거나 실패면 예외/ null.</summary>
    public static async Task<string?> ResolvePrimaryOrgIdAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SkillsTimeout);
        using var http = new HttpClient { Timeout = SkillsTimeout };
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/organizations");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("organizations", out var orgs)
            || orgs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? first = null;
        foreach (var o in orgs.EnumerateArray())
        {
            if (!o.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            first ??= id;
            if (o.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True)
            {
                return id;
            }
        }

        return first;
    }

    /// <summary>orgId 의 팀 공유 스킬 목록 (사용자가 활성화한 것만 서버가 반환). 실패는 예외.</summary>
    public static async Task<IReadOnlyList<TeamSkillItem>> GetSkillsAsync(
        string baseUrl, string apiKey, string orgId, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SkillsTimeout);
        using var http = new HttpClient { Timeout = SkillsTimeout };
        using var req = new HttpRequestMessage(HttpMethod.Get,
            baseUrl.TrimEnd('/') + "/skills?orgId=" + Uri.EscapeDataString(orgId));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}");
        }

        var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var result = new List<TeamSkillItem>();
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var it in items.EnumerateArray())
        {
            string? Str(string k) =>
                it.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var name = Str("name");
            var body = Str("body");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(body))
            {
                continue; // 이름/본문 없는 항목은 스킬로 성립하지 않음.
            }

            result.Add(new TeamSkillItem(name!, Str("description") ?? string.Empty, body!));
        }

        return result;
    }

    private async Task<LoginResult> PostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _host + path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(text);
        }
        catch (Exception ex)
        {
            return new LoginResult("error", null, null, null, Array.Empty<string>(), null, L10n.Get("config.connectFailed", ex.Message), null);
        }
    }

    private static LoginResult Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;

            string? Str(string k) =>
                r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var models = new List<string>();
            if (r.TryGetProperty("models", out var ms) && ms.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in ms.EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.String)
                    {
                        models.Add(m.GetString()!);
                    }
                    else if (m.ValueKind == JsonValueKind.Object
                             && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        models.Add(id.GetString()!);
                    }
                }
            }

            return new LoginResult(
                Str("status") ?? "error",
                Str("apiKey"),
                Str("baseUrl"),
                Str("defaultModel"),
                models,
                Str("mfaToken"),
                Str("error") ?? Str("message"),
                Str("orgName"),
                Str("name"));
        }
        catch
        {
            var preview = text.Length > 200 ? text[..200] : text;
            return new LoginResult("error", null, null, null, Array.Empty<string>(), null, L10n.Get("config.parseFailed", preview), null);
        }
    }
}
