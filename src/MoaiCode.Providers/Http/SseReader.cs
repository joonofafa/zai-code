using System.Runtime.CompilerServices;

namespace MoaiCode.Providers.Http;

using MoaiCode.Providers;
using MoaiCode.Localization;

/// <summary>
/// Server-Sent Events 디코더 (TS의 TextDecoderStream + SSE 파서 대응).
/// chat/completions 스트림의 "data:" 페이로드만 추출해 순차 방출.
/// </summary>
public static class SseReader
{
    public static async IAsyncEnumerable<string> ReadDataLinesAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var idleTimeout = IdleReadTimeout();
        using var reader = new StreamReader(stream);
        while (true)
        {
            string? line;

            // idle-read 타임아웃: HttpClient 는 무제한(SharedHttp.Timeout=Infinite)이라 연결이 살아있는
            // 채 데이터만 안 오는 silent stall 을 애플리케이션 계층에서 막을 방어가 없었다 — 프록시/게이트웨이가
            // 스트림을 끊지도 않고 굳어버리면 프로세스가 영원히 멈췄다. 매 줄마다 ct 에 연결된 CTS 를 새로
            // 만들어 한 줄 도착에 상한(기본 120s)을 건다. 발동하면 transient 로 변환해 RetryingChatModel 의
            // 기존 재시도(토큰 방출 전=백오프 재시도, 이후=즉시 실패)에 자연스럽게 올라탄다.
            using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idleCts.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(idleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 원 호출자 ct 가 아니라 idle timeout 이 원인 → 무한 대기를 유한한 실패로.
                    throw new ProviderException(
                        L10n.Get("providers.streamIdleTimeout", (int)idleTimeout.TotalSeconds),
                        ErrorCategory.NetworkTransient);
                }
                catch (Exception ex) when (ProviderException.IsNetworkFailure(ex))
                {
                    // 스트림이 [DONE] 전에 끊김(ResponseEnded 등) → transient 로 변환해 재시도 계층이 처리.
                    throw new ProviderException(
                        L10n.Get("providers.streamEndedEarly", ex.Message), ErrorCategory.NetworkTransient, ex);
                }
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0 || line[0] == ':')
            {
                continue; // 빈 줄 / 주석
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                yield return line[5..].TrimStart();
            }
            // event:/id:/retry: 라인은 chat completions에서 불필요 → 무시
        }
    }

    // SSE 한 줄 도착 상한. env MOAI_STREAM_IDLE_TIMEOUT_SECONDS 로 조정(기본 120s, 0=비활성).
    // reasoning 모델은 첫 토큰까지 몇 분씩 침묵할 수 있어 너무 짧으면 정상 스트림을 끊는다.
    // 합리적 범위로 클램프(10s~1h).
    private static TimeSpan IdleReadTimeout()
    {
        var env = Environment.GetEnvironmentVariable("MOAI_STREAM_IDLE_TIMEOUT_SECONDS");
        if (int.TryParse(env, out var n))
        {
            if (n <= 0)
            {
                return Timeout.InfiniteTimeSpan;
            }

            return TimeSpan.FromSeconds(Math.Clamp(n, 10, 3600));
        }

        return TimeSpan.FromSeconds(120);
    }
}
