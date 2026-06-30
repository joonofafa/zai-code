using System.Net;

namespace MoaiCode.Config;

/// <summary>
/// 사내망 HTTP(S) 프록시 적용. 설정(ProxyUrl/ProxyUser) + credentials(PROXY_PASSWORD)를 읽어
/// <see cref="HttpClient.DefaultProxy"/> 를 세팅한다. 기본 핸들러를 쓰는 모든 HttpClient
/// (ProviderFactory.SharedHttp, OpenMoaiClient)가 이를 자동으로 따른다.
/// → 시작 시 1회 호출(어떤 HttpClient 사용보다 먼저).
/// </summary>
public static class ProxyConfig
{
    /// <summary>설정에서 프록시를 읽어 적용. 적용된 프록시 URL을 반환(없으면 null).</summary>
    public static string? Apply(string workingDirectory, ICredentialStore? credentials = null)
    {
        var settings = SettingsLoader.Load(workingDirectory);
        return Apply(settings.ProxyUrl, settings.ProxyUser, credentials);
    }

    /// <summary>명시적 값으로 적용 (테스트/직접 호출용).</summary>
    public static string? Apply(string? proxyUrl, string? proxyUser, ICredentialStore? credentials = null)
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

        if (!string.IsNullOrWhiteSpace(proxyUser))
        {
            var pw = (credentials ?? new FileCredentialStore()).Get("PROXY_PASSWORD") ?? string.Empty;
            proxy.Credentials = new NetworkCredential(proxyUser, pw);
        }

        HttpClient.DefaultProxy = proxy;
        return proxyUrl;
    }
}
