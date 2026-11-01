using System.Text.Json;
using MoaiCode.Core;
using MoaiCode.Localization;

namespace MoaiCode.Providers;

/// <summary>모델 목록 조회 결과. 실패하면 Models 는 비고 Error 에 이유가 담긴다.</summary>
public sealed record ModelListResult(IReadOnlyList<string> Models, string? Error);

/// <summary>
/// 프로바이더의 모델 목록 조회. /provider 는 이것으로 키·주소가 맞는지 확인한 뒤 모델을 고르게 한다
/// (그래서 실패 이유 — 401 등 — 를 삼키지 않고 돌려준다). OpenAI 호환 4종은 GET {base}/models,
/// Claude 는 같은 경로에 x-api-key·anthropic-version 헤더를 쓴다.
/// </summary>
public static class ModelDirectory
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    // OpenAI 목록에는 대화에 못 쓰는 모델(임베딩·음성·이미지 등)이 섞여 있다.
    private static readonly string[] NonChatMarkers =
        ["embedding", "tts", "whisper", "dall-e", "moderation", "transcribe", "realtime", "audio", "image"];

    public static Task<ModelListResult> ListAsync(ProviderProfile profile, CancellationToken ct)
        => ListAsync(SharedHttp, profile.Kind, profile.BaseUrl, profile.ApiKey, ct);

    internal static async Task<ModelListResult> ListAsync(
        HttpClient http, ProviderKind kind, string? baseUrl, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseUrl))
        {
            return new([], L10n.Get("providers.list.noBaseUrl"));
        }

        try
        {
            var url = baseUrl.TrimEnd('/') + (kind == ProviderKind.Anthropic ? "/models?limit=1000" : "/models");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (kind == ProviderKind.Anthropic)
            {
                req.Headers.TryAddWithoutValidation("x-api-key", apiKey ?? string.Empty);
                req.Headers.TryAddWithoutValidation("anthropic-version", Anthropic.AnthropicChatModel.ApiVersion);
            }
            else if (!string.IsNullOrEmpty(apiKey))
            {
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }

            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new([], L10n.Get("providers.list.http", (int)resp.StatusCode, Shorten(text)));
            }

            var ids = Parse(text)
                .Where(id => kind != ProviderKind.OpenAI || !NonChatMarkers.Any(m => id.Contains(m, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return ids.Count == 0 ? new([], L10n.Get("providers.list.empty")) : new(ids, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)   // 사용자 취소만 그대로 올린다(시간 초과·잘못된 주소 등은 이유로)
        {
            return new([], L10n.Get("providers.list.failed", ex.Message));
        }
    }

    // OpenAI 형식 { "data": [ { "id": "..." } ] } · 최상위 배열(문자열/객체) 모두 받는다.
    internal static IEnumerable<string> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var arr = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : default;
        if (arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var ids = new List<string>();
        foreach (var m in arr.EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.String)
            {
                ids.Add(m.GetString()!);
            }
            else if (m.ValueKind == JsonValueKind.Object
                     && m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                ids.Add(id.GetString()!);
            }
        }

        return ids;
    }

    private static string Shorten(string body)
    {
        var flat = body.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}
