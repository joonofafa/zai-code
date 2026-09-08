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

    /// <summary>공식 z.ai Coding Plan 엔드포인트인지. Coding Plan 키는 여기서만 유효하다.</summary>
    public static bool IsOfficial(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Host.Equals("api.z.ai", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.TrimEnd('/').Equals(
               "/api/coding/paas/v4", StringComparison.OrdinalIgnoreCase);
}
