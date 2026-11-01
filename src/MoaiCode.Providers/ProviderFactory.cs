using MoaiCode.Core;
using MoaiCode.Core.Agent;
using MoaiCode.Localization;
using MoaiCode.Providers.Anthropic;
using MoaiCode.Providers.OpenAi;

namespace MoaiCode.Providers;

/// <summary>
/// 프로바이더 프로필로 대화 모델을 만든다. Z.ai·OpenAI·OpenRouter·vLLM 은 OpenAI 호환 chat/completions, Claude 는 Messages API.
/// 요청을 보낼 수 없는 프로필(키·모델·주소 누락)은 오프라인 EchoChatModel 로 대신하고 이유를 설명에 담는다.
/// </summary>
public static class ProviderFactory
{
    // 스트리밍 응답을 위해 타임아웃 무제한 (개별 요청은 CancellationToken으로 제어).
    private static readonly HttpClient SharedHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static IChatModel CreateDefault(out string description)
        => Create(ActiveProvider.Current, out description);

    public static IChatModel Create(ProviderProfile profile, out string description)
    {
        var name = ProviderCatalog.DisplayName(profile.Kind);
        if (!profile.IsUsable)
        {
            description = L10n.Get("providers.echoOffline", name, Missing(profile));
            return new EchoChatModel();
        }

        description = $"{name} · {profile.Model} @ {profile.BaseUrl}";
        if (profile.Kind == ProviderKind.Anthropic)
        {
            return new RetryingChatModel(new AnthropicChatModel(SharedHttp, profile.BaseUrl!, profile.ApiKey!, profile.Model!));
        }

        return new RetryingChatModel(
            new OpenAiChatModel(SharedHttp, profile.BaseUrl!, profile.ApiKey ?? string.Empty, profile.Model!, profile.Kind));
    }

    // 무엇이 빠졌는지 — 상태줄·/model 안내에 그대로 보여 준다.
    private static string Missing(ProviderProfile p)
        => string.IsNullOrEmpty(p.BaseUrl) ? "base URL"
            : string.IsNullOrEmpty(p.Model) ? "model"
            : ProviderCatalog.KeyName(p.Kind);
}
