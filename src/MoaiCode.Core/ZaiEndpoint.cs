namespace MoaiCode.Core;

/// <summary>
/// z.ai 접속 정보(키·베이스 URL·모델)를 한 곳에서 해석한다.
/// 대화 모델(ProviderFactory)과 WebSearch 가 같은 계정·같은 엔드포인트를 쓰므로, 규칙이 갈리면
/// 한쪽만 조용히 죽는다(실제로 겪음: 런처는 OPENAI_*, 코드는 ZAI_* 를 봐서 검색만 비활성).
/// 베이스 URL 화이트리스트도 여기 하나만 둔다 — 설정에 남은 옛 사내 주소로 새어나가지 않게.
/// </summary>
public static class ZaiEndpoint
{
    public const string DefaultBaseUrl = "https://api.z.ai/api/coding/paas/v4";
    public const string DefaultModel = "glm-5.3";

    /// <summary>API 키. 없으면 null(호출부가 폴백/비활성 결정).</summary>
    public static string? ApiKey()
    {
        var key = Environment.GetEnvironmentVariable("ZAI_API_KEY");
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    /// <summary>베이스 URL. 공식 z.ai 코딩 엔드포인트가 아니면 기본값으로 되돌린다.</summary>
    public static string BaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("ZAI_BASE_URL");
        return (IsOfficial(configured) ? configured! : DefaultBaseUrl).TrimEnd('/');
    }

    /// <summary>모델 id. MOAI_MODEL(런타임 /model 전환) → ZAI_MODEL → 기본값.</summary>
    public static string Model()
        => Environment.GetEnvironmentVariable("MOAI_MODEL")
           ?? Environment.GetEnvironmentVariable("ZAI_MODEL")
           ?? DefaultModel;

    /// <summary>비전(이미지 이해) 전용 모델 id. MOAI_VISION_MODEL → ZAI_VISION_MODEL → 기본값.</summary>
    public static string VisionModel()
        => Environment.GetEnvironmentVariable("MOAI_VISION_MODEL")
           ?? Environment.GetEnvironmentVariable("ZAI_VISION_MODEL")
           ?? DefaultVisionModel;

    /// <summary>기본 비전 모델. 코딩 엔드포인트·코딩 키로 그대로 호출 검증됨(2026-09-12).</summary>
    public const string DefaultVisionModel = "glm-4.6v";

    /// <summary>
    /// 모델별 컨텍스트 창(토큰). 공식 문서 기준(2026-09-20): docs.z.ai/guides/llm, /guides/vlm.
    /// /models API는 스펙 메타데이터를 주지 않아 내장 테이블로 유지한다. 새 모델은 여기 추가.
    /// 미등록 모델은 <see cref="FallbackContextWindow"/>를 쓴다.
    /// </summary>
    private static readonly Dictionary<string, int> ContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glm-5.3"] = 1_000_000,
        ["glm-5.3-flash"] = 1_000_000,
        ["glm-5.3-flashx"] = 1_000_000,
        ["glm-5.2"] = 1_000_000,
        ["glm-5.1"] = 200_000,
        ["glm-5"] = 200_000,
        ["glm-5-turbo"] = 200_000,      // 문서 미고시 — glm-5 와 같은 세대로 추정
        ["glm-4.7"] = 200_000,          // 문서 미고시 — glm-4.6(200K) 후속으로 추정
        ["glm-4.6"] = 200_000,
        ["glm-4.6v"] = 200_000,          // 비전 모델도 4.6 세대와 동일
        ["glm-4.5"] = 128_000,
        ["glm-4.5-air"] = 128_000,
    };

    /// <summary>테이블에 없는 모델의 컨텍스트 창. 보수적 하한.</summary>
    public const int FallbackContextWindow = 200_000;

    /// <summary>모델 id → 컨텍스트 창. 등록되지 않은 id 는 폴백 값을 돌려준다.</summary>
    public static int ContextWindow(string? modelId)
        => modelId is not null && ContextWindows.TryGetValue(modelId, out var w) ? w : FallbackContextWindow;

    /// <summary>공식 z.ai Coding Plan 엔드포인트인지. Coding Plan 키는 여기서만 유효하다.</summary>
    public static bool IsOfficial(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("api.z.ai", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.TrimEnd('/').Equals(
               "/api/coding/paas/v4", StringComparison.OrdinalIgnoreCase);
}
