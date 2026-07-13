using System.Collections.Concurrent;

namespace MoaiCode.Tools.Office;

/// <summary>
/// COM 호출을 전용 단일 스레드에서 직렬화한다(원본 설계: docu_work_interaction.md §COM 실행 모델).
/// Windows 에서는 STA 아파트로 실행한다 — Office COM 은 STA 를 요구한다. STA 가 없는 플랫폼에서는
/// 일반 전용 스레드로 실행한다(큐/직렬화 로직은 동일하므로 Linux 에서 테스트 가능).
///
/// COM 객체는 이 스레드 밖으로 넘기지 않는다. 호출부는 <see cref="InvokeAsync{T}"/> 안에서 DTO 로
/// 변환한 결과만 받는다.
/// </summary>
public sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public StaDispatcher(string name = "office-com")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows())
        {
            _thread.SetApartmentState(ApartmentState.STA);
        }

        _thread.Start();
    }

    /// <summary>실제 STA 로 도는지(=Windows). 진단/테스트용.</summary>
    public bool IsSta => OperatingSystem.IsWindows();

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
    }

    /// <summary>func 를 전용 스레드에서 실행하고 결과를 기다린다(직렬화됨).</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    tcs.SetResult(func());
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            // Dispose 후 큐가 닫힘.
            tcs.SetException(new ObjectDisposedException(nameof(StaDispatcher)));
        }

        return tcs.Task;
    }

    public Task InvokeAsync(Action action) =>
        InvokeAsync(() => { action(); return true; });

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_thread.IsAlive && !_thread.Equals(Thread.CurrentThread))
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}
