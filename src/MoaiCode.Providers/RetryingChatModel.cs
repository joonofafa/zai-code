using System.Runtime.CompilerServices;
using MoaiCode.Core.Agent;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

namespace MoaiCode.Providers;

/// <summary>
/// IChatModel 데코레이터: 스트림이 시작되기 전(첫 이벤트 방출 전)에 발생한
/// transient 오류(429/529/5xx)를 지수 백오프로 재시도 (TS withRetry 축약판).
/// 일단 토큰이 방출되면 재시도하지 않음(부분 응답 중복 방지).
/// </summary>
public sealed class RetryingChatModel : IChatModel, IModelControl
{
    private readonly IChatModel _inner;
    private readonly int _maxRetries;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <summary>내부 모델이 전환을 지원하면 위임 (아니면 no-op/빈 목록).</summary>
    public string CurrentModel
    {
        get => (_inner as IModelControl)?.CurrentModel ?? "";
        set { if (_inner is IModelControl c) c.CurrentModel = value; }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
        => _inner is IModelControl c
            ? c.ListModelsAsync(ct)
            : Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public RetryingChatModel(
        IChatModel inner,
        int maxRetries = 5,
        Func<int, TimeSpan>? backoff = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inner = inner;
        _maxRetries = maxRetries;
        // 지수 백오프(500ms→8s 상한). 5회면 ~0.5+1+2+4+8 ≈ 15.5초에 걸쳐 재시도 →
        // 스트림이 시작 전에 끊기는 몇 초~십수 초짜리 네트워크/게이트웨이 블립을 넘긴다.
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(Math.Min(8000, 500 * Math.Pow(2, attempt))));
        _delay = delay ?? Task.Delay;
    }

    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ITool> tools,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var enumerator = _inner.StreamAsync(messages, tools, ct).GetAsyncEnumerator(ct);
            var yielded = false;
            var retry = false;

            try
            {
                while (true)
                {
                    StreamEvent current;
                    try
                    {
                        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (ProviderException ex) when (ex.IsTransient && !yielded && attempt < _maxRetries)
                    {
                        retry = true;
                        break;
                    }

                    yielded = true;
                    yield return current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (!retry)
            {
                yield break;
            }

            await _delay(_backoff(attempt), ct).ConfigureAwait(false);
        }
    }
}
