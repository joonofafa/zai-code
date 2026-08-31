using MoaiCode.Core.Agent;
using MoaiCode.Localization;
using MoaiCode.Providers.OpenAi;

namespace MoaiCode.Providers;

/// <summary>
/// Z.ai 환경변수 기반 모델 생성.
/// Z.ai의 OpenAI-compatible wire format을 사용하지만, 요청 대상은 항상 Z.ai API이다.
/// 키가 없으면 오프라인 EchoChatModel로 폴백한다.
/// </summary>
public static class ProviderFactory
{
    public const string DefaultBaseUrl = "https://api.z.ai/api/coding/paas/v4";

    // 스트리밍 응답을 위해 타임아웃 무제한 (개별 요청은 CancellationToken으로 제어).
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static IChatModel CreateDefault(out string description)
    {
        var key = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            description = L10n.Get("providers.echoOffline");
            return new EchoChatModel();
        }

        var configuredBaseUrl = Environment.GetEnvironmentVariable("ZAI_BASE_URL");
        var baseUrl = IsOfficialZaiBaseUrl(configuredBaseUrl) ? configuredBaseUrl! : DefaultBaseUrl;
        baseUrl = baseUrl.TrimEnd('/');
        var model = Environment.GetEnvironmentVariable("MOAI_MODEL")
                    ?? Environment.GetEnvironmentVariable("ZAI_MODEL")
                    ?? "glm-5.3";

        description = $"Z.ai · {model} @ {baseUrl}";
        return new RetryingChatModel(new OpenAiChatModel(SharedHttp, baseUrl, key, model));
    }

    private static bool IsOfficialZaiBaseUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("api.z.ai", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.TrimEnd('/').Equals(
               "/api/coding/paas/v4", StringComparison.OrdinalIgnoreCase);
}
