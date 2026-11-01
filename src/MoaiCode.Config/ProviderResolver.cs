using MoaiCode.Core;

namespace MoaiCode.Config;

/// <summary>
/// 설정·저장된 키·환경변수에서 현재 프로바이더 접속 정보를 만드는 유일한 지점.
/// 우선순위: 실행 인자(--model) → /provider·/model 저장값 → 환경변수(런처가 넣는 값) → 기본값.
/// 예전엔 런처가 MOAI_MODEL 을 늘 내보내고 환경변수가 설정을 이겨 /model 선택이 재시작하면 사라졌다.
/// </summary>
public static class ProviderResolver
{
    public static ProviderProfile Resolve(Settings settings, ICredentialStore credentials, string? modelOverride = null)
    {
        var kind = ProviderCatalog.Parse(settings.ActiveProvider) ?? ProviderKind.Zai;
        var saved = settings.Providers.TryGetValue(ProviderCatalog.Id(kind), out var p) ? p : null;
        var keyName = ProviderCatalog.KeyName(kind);
        var key = NonEmpty(credentials.Get(keyName)) ?? NonEmpty(Environment.GetEnvironmentVariable(keyName));

        string? baseUrl;
        string? model;
        string? vision;
        if (kind == ProviderKind.Zai)
        {
            baseUrl = ProviderCatalog.NormalizeBaseUrl(kind, saved?.BaseUrl) ?? ZaiEndpoint.BaseUrl();
            model = saved?.Model ?? settings.Model
                    ?? NonEmpty(Environment.GetEnvironmentVariable("MOAI_MODEL"))
                    ?? NonEmpty(Environment.GetEnvironmentVariable("ZAI_MODEL"))
                    ?? ZaiEndpoint.DefaultModel;
            vision = saved?.VisionModel ?? ZaiEndpoint.VisionModel();
        }
        else
        {
            baseUrl = ProviderCatalog.NormalizeBaseUrl(kind, saved?.BaseUrl)
                      ?? (kind == ProviderKind.Vllm
                          ? ProviderCatalog.NormalizeBaseUrl(kind, Environment.GetEnvironmentVariable("VLLM_BASE_URL"))
                          : ProviderCatalog.DefaultBaseUrl(kind));
            // 다른 프로바이더는 MOAI_MODEL 을 보지 않는다 — 옛 런처가 내보내는 glm-5.3 이 OpenAI 등으로 새지 않게.
            model = saved?.Model;
            vision = saved?.VisionModel;
        }

        var chosen = NonEmpty(modelOverride) ?? model;
        return new ProviderProfile(
            kind,
            baseUrl,
            key,
            chosen,
            NonEmpty(vision),   // 빈 문자열 = 이미지 분석 사용 안 함
            // 저장된 창 크기는 그 모델의 것이다 — --model 로 다른 모델을 쓰면 기본값으로.
            string.Equals(chosen, saved?.Model, StringComparison.Ordinal) ? saved?.ContextWindow : null);
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
