namespace MoaiCode.Core;

/// <summary>대화 모델을 제공하는 LLM 프로바이더. /provider 가 고른다.</summary>
public enum ProviderKind
{
    Zai,
    OpenAI,
    Anthropic,
    OpenRouter,
    Vllm,
}

/// <summary>
/// 프로바이더별 고정 정보(설정 id·표시 이름·기본 주소·키 이름)의 유일한 정의 지점.
/// 키 이름은 credentials.json 의 항목 이름이자 저장값이 없을 때 읽는 환경변수 이름이다.
/// </summary>
public static class ProviderCatalog
{
    /// <summary>/provider 목록 순서.</summary>
    public static IReadOnlyList<ProviderKind> All { get; } =
        [ProviderKind.Zai, ProviderKind.OpenAI, ProviderKind.Anthropic, ProviderKind.OpenRouter, ProviderKind.Vllm];

    /// <summary>settings.json 의 activeProvider·providers 키에 쓰는 id.</summary>
    public static string Id(ProviderKind kind) => kind switch
    {
        ProviderKind.Zai => "zai",
        ProviderKind.OpenAI => "openai",
        ProviderKind.Anthropic => "anthropic",
        ProviderKind.OpenRouter => "openrouter",
        _ => "vllm",
    };

    public static string DisplayName(ProviderKind kind) => kind switch
    {
        ProviderKind.Zai => "Z.ai",
        ProviderKind.OpenAI => "OpenAI",
        ProviderKind.Anthropic => "Claude",
        ProviderKind.OpenRouter => "OpenRouter",
        _ => "vLLM",
    };

    /// <summary>id → 종류. 모르는 값은 null.</summary>
    public static ProviderKind? Parse(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "zai" or "z.ai" => ProviderKind.Zai,
        "openai" => ProviderKind.OpenAI,
        "anthropic" or "claude" => ProviderKind.Anthropic,
        "openrouter" => ProviderKind.OpenRouter,
        "vllm" => ProviderKind.Vllm,
        _ => null,
    };

    /// <summary>API 키 이름(credentials.json 항목 = 환경변수 폴백).</summary>
    public static string KeyName(ProviderKind kind) => kind switch
    {
        ProviderKind.Zai => "ZAI_API_KEY",
        ProviderKind.OpenAI => "OPENAI_API_KEY",
        ProviderKind.Anthropic => "ANTHROPIC_API_KEY",
        ProviderKind.OpenRouter => "OPENROUTER_API_KEY",
        _ => "VLLM_API_KEY",
    };

    /// <summary>기본 API 주소. vLLM 은 사내 서버라 기본값이 없다(사용자가 입력).</summary>
    public static string? DefaultBaseUrl(ProviderKind kind) => kind switch
    {
        ProviderKind.Zai => ZaiEndpoint.DefaultBaseUrl,
        ProviderKind.OpenAI => "https://api.openai.com/v1",
        ProviderKind.Anthropic => "https://api.anthropic.com/v1",
        ProviderKind.OpenRouter => "https://openrouter.ai/api/v1",
        _ => null,
    };

    /// <summary>
    /// 모델 목록에서 창 크기를 알 수 없을 때 쓰는 보수적인 컨텍스트 창. z.ai 는 모델별 표(ZaiEndpoint)를 쓴다.
    /// vLLM 은 서버마다 다르지만 32K 로 띄우는 경우가 흔하다 — 크게 잡으면 넘친 뒤에야 압축한다.
    /// </summary>
    public static int DefaultContextWindow(ProviderKind kind) => kind switch
    {
        ProviderKind.Zai => ZaiEndpoint.FallbackContextWindow,
        ProviderKind.Anthropic => 200_000,
        ProviderKind.Vllm => 32_768,
        _ => 128_000,
    };

    /// <summary>키 없이도 쓸 수 있는지. 사내 vLLM 은 인증 없이 열려 있는 경우가 많다.</summary>
    public static bool RequiresKey(ProviderKind kind) => kind != ProviderKind.Vllm;

    /// <summary>
    /// 설정의 API 주소를 검증한다. z.ai 는 공식 코딩 엔드포인트만(Coding Plan 키가 다른 주소로 새지 않게),
    /// 나머지는 절대 http(s) 주소만 받는다. 받을 수 없으면 null.
    /// </summary>
    public static string? NormalizeBaseUrl(ProviderKind kind, string? value)
    {
        if (kind == ProviderKind.Zai)
        {
            return ZaiEndpoint.IsOfficial(value) ? value!.Trim().TrimEnd('/') : null;
        }

        return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? value!.Trim().TrimEnd('/')
            : null;
    }
}

/// <summary>지금 대화에 쓰는 프로바이더 접속 정보. 해석은 Config 의 ProviderResolver 가 한다.</summary>
/// <param name="VisionModel">이미지 분석 모델. null 이면 이미지 분석을 쓰지 않는다.</param>
/// <param name="ContextWindow">모델의 컨텍스트 창(토큰). null 이면 프로바이더 기본값.</param>
public sealed record ProviderProfile(
    ProviderKind Kind,
    string? BaseUrl,
    string? ApiKey,
    string? Model,
    string? VisionModel,
    int? ContextWindow = null)
{
    /// <summary>컴팩션 기준이 될 컨텍스트 창. z.ai 는 모델별 표, 그 밖은 저장값 → 프로바이더 기본값.</summary>
    public int EffectiveContextWindow =>
        ContextWindow is > 0 ? ContextWindow.Value
        : Kind == ProviderKind.Zai ? ZaiEndpoint.ContextWindow(Model)
        : ProviderCatalog.DefaultContextWindow(Kind);

    /// <summary>요청을 보낼 수 있는 상태인지(주소·모델·필요한 키가 모두 있음).</summary>
    public bool IsUsable =>
        !string.IsNullOrEmpty(BaseUrl)
        && !string.IsNullOrEmpty(Model)
        && (!string.IsNullOrEmpty(ApiKey) || !ProviderCatalog.RequiresKey(Kind));
}

/// <summary>
/// 현재 프로바이더. CLI 가 시작 시·/provider·/model 전환 시 설정하고, 대화 모델 외에 같은 접속 정보를 쓰는
/// 툴(WebSearch·이미지)이 읽는다. 설정 전에는 환경변수의 z.ai 정보로 대신한다(테스트·SDK 경로).
/// </summary>
public static class ActiveProvider
{
    private static ProviderProfile? _current;

    public static ProviderProfile Current
    {
        get => Volatile.Read(ref _current) ?? FromEnvironment();
        set => Volatile.Write(ref _current, value);
    }

    /// <summary>설정값을 지워 환경변수 폴백으로 되돌린다(테스트용).</summary>
    public static void Reset() => Volatile.Write(ref _current, null);

    /// <summary>환경변수만으로 만든 z.ai 프로필.</summary>
    public static ProviderProfile FromEnvironment() => new(
        ProviderKind.Zai,
        ZaiEndpoint.BaseUrl(),
        ZaiEndpoint.ApiKey(),
        ZaiEndpoint.Model(),
        ZaiEndpoint.VisionModel());
}
