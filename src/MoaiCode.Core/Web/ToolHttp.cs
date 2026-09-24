using System.Net;
using System.Net.Http;

namespace MoaiCode.Core.Web;

/// <summary>
/// 툴용 공용 HttpClient 팩토리. 호출마다 SocketsHttpHandler 를 새로 만들던 6곳을 통일했다.
/// 핸들러(연결 풀)만 공유하고 클라이언트는 per-call 로 만든다 — 툴마다 Authorization/UserAgent
/// 헤더를 다르게 싣기 때문에 클라이언트를 공유하면 헤더가 오염된다. 소켓은 핸들러의
/// 연결 풀에서 재사용되므로 TIME_WAIT 적체도 사라진다.
///
/// 두 종류의 핸들러:
/// - <see cref="ApiHandler"/>: 자동 압축해제만(API 호출용 — WebSearch·ImageCreate·ImageAnalysis 질의).
/// - <see cref="DownloadHandler"/>: +리다이렉트 비활성(다운로드용 — WebFetch·ImageFetch·이미지 다운로드).
///   리다이렉트는 툴이 수동 추적하며 매 홉 SSRF 재검증(GuardSsrfAsync)을 돌린다.
///
/// 프록시는 미지정 → HttpClient.DefaultProxy(ProxyConfig 가 시작 시 세팅)를 자동으로 따른다.
/// </summary>
public static class ToolHttp
{
    /// <summary>API 호출용(리다이렉트 자동). using 과 함께 per-call 클라이언트 생성.</summary>
    public static HttpClient CreateApi() => new(ApiHandler, disposeHandler: false);

    /// <summary>다운로드용(리다이렉트 수동 — SSRF 홉별 재검증). using 과 함께 per-call 클라이언트 생성.</summary>
    public static HttpClient CreateDownloader() => new(DownloadHandler, disposeHandler: false);

    private static readonly SocketsHttpHandler ApiHandler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        // 요청-수명과 무관하게 연결 풀 유지(클라이언트 dispose 후에도 소켓 재사용).
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    private static readonly SocketsHttpHandler DownloadHandler = new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };
}
