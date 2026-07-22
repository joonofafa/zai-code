using System.Net;
using System.Text.RegularExpressions;

namespace MoaiCode.Config;

/// <summary>
/// 사내망 HTTP(S) 프록시 적용. 설정(ProxyUrl/ProxyUser) + credentials(PROXY_PASSWORD)를 읽어
/// <see cref="HttpClient.DefaultProxy"/> 를 세팅한다. 기본 핸들러를 쓰는 모든 HttpClient
/// (ProviderFactory.SharedHttp, OpenMoaiClient)가 이를 자동으로 따른다.
/// → 시작 시 1회 호출(어떤 HttpClient 사용보다 먼저).
///
/// **LLM 게이트웨이(BaseUrl 호스트)는 항상 프록시를 우회한다.** 사내 프록시가 장기 SSE
/// 스트리밍 연결을 일정 시간(예: 60초)에 끊어 "응답 스트림이 완료 전에 끊겼습니다"를 유발하기
/// 때문. 게이트웨이는 보통 내부 호스트라 직결 가능하다. NO_PROXY 환경변수·설정도 병합한다.
/// </summary>
public static class ProxyConfig
{
    /// <summary>설정에서 프록시를 읽어 적용. 적용된 프록시 URL을 반환(없으면 null).</summary>
    public static string? Apply(string workingDirectory, ICredentialStore? credentials = null)
    {
        var settings = SettingsLoader.Load(workingDirectory);
        return Apply(settings.ProxyUrl, settings.ProxyUser, credentials, BuildBypass(settings.BaseUrl, settings.ProxyBypass));
    }

    /// <summary>명시적 값으로 적용 (테스트/직접 호출용).</summary>
    public static string? Apply(
        string? proxyUrl, string? proxyUser, ICredentialStore? credentials = null, IEnumerable<string>? bypass = null)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var proxy = new WebProxy(uri) { BypassProxyOnLocal = true };

        var bypassList = (bypass ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (bypassList.Length > 0)
        {
            proxy.BypassList = bypassList;
        }

        if (!string.IsNullOrWhiteSpace(proxyUser))
        {
            var pw = (credentials ?? new FileCredentialStore()).Get("PROXY_PASSWORD") ?? string.Empty;
            proxy.Credentials = new NetworkCredential(proxyUser, pw);
        }

        HttpClient.DefaultProxy = proxy;
        return proxyUrl;
    }

    /// <summary>우회할 호스트들을 WebProxy.BypassList 정규식으로 만든다(게이트웨이 + NO_PROXY + 설정).</summary>
    public static string[] BuildBypass(string? baseUrl, string? proxyBypass)
    {
        var hosts = new List<string>();

        // 1) LLM 게이트웨이(BaseUrl 호스트) — 항상 우회(사내 프록시의 SSE 타임아웃 회피).
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var b))
        {
            hosts.Add(b.Host);
        }

        // 2) 표준 NO_PROXY 환경변수.
        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                      ?? Environment.GetEnvironmentVariable("no_proxy");
        if (!string.IsNullOrWhiteSpace(noProxy))
        {
            hosts.AddRange(noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // 3) 사용자 지정 proxyBypass.
        if (!string.IsNullOrWhiteSpace(proxyBypass))
        {
            hosts.AddRange(proxyBypass.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return hosts
            .Select(h => h.TrimStart('.'))
            .Where(h => h.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(HostToRegex)
            .ToArray();
    }

    // WebProxy.BypassList 는 요청 URI 에 대한 정규식. 호스트(+선택적 서브도메인/포트)를 매칭.
    private static string HostToRegex(string host) =>
        $@"://([^/@]+\.)?{Regex.Escape(host)}(:\d+)?(/|$)";
}
