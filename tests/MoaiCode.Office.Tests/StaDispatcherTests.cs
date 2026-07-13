using MoaiCode.Tools.Office;
using Xunit;

namespace MoaiCode.Office.Tests;

// STA 디스패처의 큐/직렬화/예외전파 로직(플랫폼 무관 부분). Windows 에서는 실제 STA,
// Linux 에서는 일반 전용 스레드로 동일 로직 검증.
public sealed class StaDispatcherTests
{
    [Fact]
    public async Task Runs_work_and_returns_result()
    {
        using var d = new StaDispatcher();
        Assert.Equal(42, await d.InvokeAsync(() => 42));
    }

    [Fact]
    public async Task Serializes_all_work_onto_one_thread()
    {
        using var d = new StaDispatcher();
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();
        var tasks = Enumerable.Range(0, 50).Select(_ =>
            d.InvokeAsync(() => { ids.Add(Environment.CurrentManagedThreadId); return 0; }));
        await Task.WhenAll(tasks);
        Assert.Single(ids.Distinct()); // 모든 작업이 같은 단일 스레드에서 실행
    }

    [Fact]
    public async Task Propagates_exceptions()
    {
        using var d = new StaDispatcher();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => d.InvokeAsync<int>(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task Throws_after_dispose()
    {
        var d = new StaDispatcher();
        d.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => d.InvokeAsync(() => 1));
    }

    [Fact]
    public void Reports_sta_only_on_windows()
    {
        using var d = new StaDispatcher();
        Assert.Equal(OperatingSystem.IsWindows(), d.IsSta);
    }
}
