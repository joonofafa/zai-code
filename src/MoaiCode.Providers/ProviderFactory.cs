using MoaiCode.Core.Agent;
using MoaiCode.Providers.OpenAi;

namespace MoaiCode.Providers;

/// <summary>
/// 환경변수 기반 프로바이더 선택 (TS providerConfig 축약판).
/// OPENAI_API_KEY가 있으면 OpenAI 호환 모델, 없으면 오프라인 EchoChatModel.
/// Phase 1+에서 base URL 추론, 자격증명 풀, Anthropic/Gemini 라우팅 확장.
/// </summary>
public static class ProviderFactory
{
    // 스트리밍 응답을 위해 타임아웃 무제한 (개별 요청은 CancellationToken으로 제어).
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static IChatModel CreateDefault(out string description)
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key))
        {
            description = "EchoChatModel (offline · OPENAI_API_KEY 미설정)";
            return new EchoChatModel();
        }

        var baseUrl = (Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                       ?? "https://api.openai.com/v1").TrimEnd('/');
        var model = Environment.GetEnvironmentVariable("MOAI_MODEL")
                    ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                    ?? "gpt-4o-mini";

        description = $"OpenAI-compatible · {model} @ {baseUrl}";
        return new RetryingChatModel(new OpenAiChatModel(SharedHttp, baseUrl, key, model));
    }
}
