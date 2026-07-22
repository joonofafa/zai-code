using System.Runtime.CompilerServices;

namespace MoaiCode.Providers.Http;

using MoaiCode.Providers;

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
        using var reader = new StreamReader(stream);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ProviderException.IsNetworkFailure(ex))
            {
                // 스트림이 [DONE] 전에 끊김(ResponseEnded 등) → transient 로 변환해 재시도 계층이 처리.
                throw new ProviderException(
                    $"응답 스트림이 완료 전에 끊겼습니다: {ex.Message}", ErrorCategory.NetworkTransient, ex);
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
}
