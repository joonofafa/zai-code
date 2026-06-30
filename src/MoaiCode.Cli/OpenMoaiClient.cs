using System.Text;
using System.Text.Json;

namespace MoaiCode.Cli;

/// <summary>CLI 로그인 응답 (open-moai /api/cli/login[/mfa]).</summary>
public sealed record LoginResult(
    string Status,
    string? ApiKey,
    string? BaseUrl,
    string? DefaultModel,
    IReadOnlyList<string> Models,
    string? MfaToken,
    string? Error);

/// <summary>open-moai 사이트의 CLI 로그인 엔드포인트 클라이언트.</summary>
public sealed class OpenMoaiClient
{
    private readonly HttpClient _http;
    private readonly string _host;

    public OpenMoaiClient(string host, HttpClient? http = null)
    {
        _host = host.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
        => PostAsync("/api/cli/login", new { email, password }, ct);

    public Task<LoginResult> LoginMfaAsync(string mfaToken, string code, CancellationToken ct)
        => PostAsync("/api/cli/login/mfa", new { mfaToken, code }, ct);

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
            return new LoginResult("error", null, null, null, Array.Empty<string>(), null, $"연결 실패: {ex.Message}");
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
                Str("error") ?? Str("message"));
        }
        catch
        {
            var preview = text.Length > 200 ? text[..200] : text;
            return new LoginResult("error", null, null, null, Array.Empty<string>(), null, $"응답 파싱 실패: {preview}");
        }
    }
}
