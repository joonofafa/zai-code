using System.Diagnostics;
using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

/// <summary>
/// 체크포인트 타임아웃 회귀. 거대한 워크스페이스에서 git 이 hang 되면 턴이 무한 정지했다.
/// 이제 상한을 넘으면 git 을 죽이고, 예외 없이 체크포인트를 건너뛰며, 세션 내 재시도를 멈춘다.
/// (느린 git 은 sleep 하는 shim 으로 재현 — Unix 전용.)
/// </summary>
public sealed class CheckpointTimeoutTests : IDisposable
{
    private readonly string _dir;
    private readonly string _shim;

    public CheckpointTimeoutTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "moai-cp-timeout-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
        _shim = Path.Combine(_dir, "slowgit");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    private void WriteSlowGit(int sleepSeconds)
    {
        File.WriteAllText(_shim, $"#!/bin/sh\nsleep {sleepSeconds}\nexit 0\n");
        var psi = new ProcessStartInfo("chmod", $"+x {_shim}") { UseShellExecute = false };
        Process.Start(psi)!.WaitForExit();
    }

    [Fact]
    public async Task Times_out_and_disables_instead_of_hanging()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // sh 스크립트 shim — Unix 전용
        }

        WriteSlowGit(sleepSeconds: 30); // 타임아웃보다 훨씬 김
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var store = new CheckpointStore(
            work, Path.Combine(_dir, ".cp"),
            gitTimeout: TimeSpan.FromSeconds(1), gitPath: _shim);

        var sw = Stopwatch.StartNew();
        var id = await store.CreateAsync("first"); // hang 되면 30초, 정상이면 ~1초
        sw.Stop();

        Assert.Equal(string.Empty, id);                       // 체크포인트 건너뜀
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),    // hang 되지 않음
            $"체크포인트가 {sw.Elapsed.TotalSeconds:0.0}초 걸림 — 상한이 동작하지 않음");

        // 한 번 타임아웃되면 이후 호출은 즉시 반환(매 툴마다 대기 방지).
        var sw2 = Stopwatch.StartNew();
        var id2 = await store.CreateAsync("second");
        sw2.Stop();
        Assert.Equal(string.Empty, id2);
        Assert.True(sw2.Elapsed < TimeSpan.FromMilliseconds(500),
            $"2차 호출이 {sw2.Elapsed.TotalMilliseconds:0}ms — 비활성화 후엔 즉시 반환해야 함");
    }

    [Fact]
    public async Task Normal_git_within_timeout_still_creates_a_checkpoint()
    {
        // 실제 git 으로 정상 동작이 깨지지 않는지(타임아웃 넉넉).
        var work = Path.Combine(_dir, "work2");
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "hi");
        var store = new CheckpointStore(
            work, Path.Combine(_dir, ".cp2"), gitTimeout: TimeSpan.FromSeconds(30));

        var id = await store.CreateAsync("normal");
        Assert.False(string.IsNullOrEmpty(id));
        var list = await store.ListAsync();
        Assert.Single(list);
    }
}
