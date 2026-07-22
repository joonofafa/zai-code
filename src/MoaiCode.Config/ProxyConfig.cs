using System.Net;
using System.Text.RegularExpressions;

namespace MoaiCode.Config;

/// <summary>
/// 사내망 HTTP(S) 프록시 적용. 설정(ProxyUrl/ProxyUser) + credentials(PROXY_PASSWORD)를 읽어
/// <see cref="HttpClient.DefaultProxy"/> 를 세팅한다. 기본 핸들러를 쓰는 모든 HttpClient
/// (ProviderFactory.SharedHttp, OpenMoaiClient)가 이를 자동으로 따른다.
/// → 시작 시 1회 호출(어떤 HttpClient 사용보다 먼저).
///
/// 우회는 **명시적(opt-in)** 이다: NO_PROXY 환경변수 + settings.proxyBypass(쉼표구분)만
/// 우회한다. 게이트웨이를 자동 우회하지 않는다 — 사내 프록시가 유일한 외부 경로인 환경에서
/// 자동 우회는 오히려 접속을 끊기 때문. (게이트웨이를 직결할 수 있는 사용자는 proxyBypass 에
/// 해당 호스트를 넣어 장기 SSE 프록시 타임아웃을 피할 수 있다.)
/// </summary>
public static class ProxyConfig
{
    /// <summary>설정에서 프록시를 읽어 적용. 적용된 프록시 URL을 반환(없으면 null).</summary>
    public static string? Apply(string workingDirectory, ICredentialStore? credentials = null)
    {
        var settings = SettingsLoader.Load(workingDirectory);
        return Apply(settings.ProxyUrl, settings.ProxyUser, credentials, BuildBypass(settings.ProxyBypass));
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

    /// <summary>우회할 호스트들을 WebProxy.BypassList 정규식으로 만든다(NO_PROXY + proxyBypass 설정).</summary>
    public static string[] BuildBypass(string? proxyBypass)
    {
        var hosts = new List<string>();

        // 표준 NO_PROXY 환경변수.
        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
                      ?? Environment.GetEnvironmentVariable("no_proxy");
        if (!string.IsNullOrWhiteSpace(noProxy))
        {
            hosts.AddRange(noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // 사용자 지정 proxyBypass (쉼표구분).
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
