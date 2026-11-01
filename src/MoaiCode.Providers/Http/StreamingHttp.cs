using MoaiCode.Localization;

namespace MoaiCode.Providers.Http;

/// <summary>스트리밍 요청 전송 공용부(OpenAI 호환·Claude 공유). 헤더 대기 상한과 연결 실패 분류를 한 곳에 둔다.</summary>
internal static class StreamingHttp
{
    /// <summary>요청을 보내고 응답 헤더까지 받는다. 요청 객체는 여기서 정리한다(응답 스트림은 호출자가 읽고 정리).</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var req = request;

        // 헤더 도착 대기 상한: SharedHttp.Timeout 은 무제한이라 방화벽/프록시가 연결을
        // 소리 없이 버리면(RST 없이 상태만 삭제 — 내부망 idle 커넥션 정리) SendAsync 가
        // 영원히 묶인다. idle-read 방어(SseReader)는 스트림 열린 뒤부터만 작동하므로
        // 이 구간에 별도 상한을 건다. 발동하면 아래 catch 가 transient 로 변환해
        // RetryingChatModel 의 기존 재시도(첫 토큰 전=백오프 재시도)에 자연스럽게 올라탄다.
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerCts.CancelAfter(HeaderWaitTimeout());
        try
        {
            var resp = await http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token)
                .ConfigureAwait(false);
            headerCts.CancelAfter(Timeout.InfiniteTimeSpan);   // 헤더 도착 — 타이머 해제(스트리밍 중 발동 금지)
            return resp;
        }
        catch (OperationCanceledException) when (headerCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // 원 호출자 취소가 아니면 헤더 대기 타임아웃 — 무한 대기를 유한한 실패로.
            throw new ProviderException(
                L10n.Get("providers.headerWaitTimeout", (int)HeaderWaitTimeout().TotalSeconds),
                ErrorCategory.NetworkTransient);
        }
        catch (Exception ex) when (ProviderException.IsNetworkFailure(ex))
        {
            // 연결 자체 실패(끊김/타임아웃/소켓) → transient 로 변환해 재시도 계층이 처리.
            throw new ProviderException(L10n.Get("providers.gatewayConnectionFailed", ex.Message), ErrorCategory.NetworkTransient, ex);
        }
    }

    // 응답 헤더(첫 바이트) 도착 대기 상한. env MOAI_HEADER_TIMEOUT_SECONDS 로 조정(기본 300s, 0=비활성).
    // idle-read(SseReader)와 달리 스트림 열리기 전 구간이라 첫 토큰 지연과 무관하게 잡을 수 있어
    // SseReader 기본(120s)보다 여유를 둔다. 클램프 10s~1h.
    private static TimeSpan HeaderWaitTimeout()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_HEADER_TIMEOUT_SECONDS");
        if (int.TryParse(env, out var n))
        {
            if (n <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }

            return TimeSpan.FromSeconds(Math.Clamp(n, 10, 3600));
        }

        return TimeSpan.FromSeconds(300);
    }
}
