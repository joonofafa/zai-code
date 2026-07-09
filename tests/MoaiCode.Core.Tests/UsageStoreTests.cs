using MoaiCode.Persistence;
using Xunit;

namespace MoaiCode.Core.Tests;

// 모델별 로컬 토큰 누적(UsageStore) — /usage 표시의 근거 데이터.
public class UsageStoreTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "moai-usage-test-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Accumulates_per_model_and_orders_by_total()
    {
        var path = TempPath();
        try
        {
            var s = new UsageStore(path);
            s.Record("small", 10, 5);
            s.Record("big", 1000, 500);
            s.Record("big", 100, 50);   // big 누적

            var all = s.All();
            Assert.Equal(2, all.Count);
            Assert.Equal("big", all[0].Model);          // 총량 많은 순
            Assert.Equal(1100, all[0].InputTokens);
            Assert.Equal(550, all[0].OutputTokens);
            Assert.Equal(2, all[0].Turns);
            Assert.Equal("small", all[1].Model);
            Assert.Equal(1, all[1].Turns);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Persists_across_instances()
    {
        var path = TempPath();
        try
        {
            new UsageStore(path).Record("gpt-x", 42, 7);
            var reloaded = new UsageStore(path).All();
            Assert.Single(reloaded);
            Assert.Equal("gpt-x", reloaded[0].Model);
            Assert.Equal(42, reloaded[0].InputTokens);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Blank_model_and_negatives_are_handled()
    {
        var path = TempPath();
        try
        {
            var s = new UsageStore(path);
            s.Record(null, -5, -3);     // 공백 모델 → "(unknown)", 음수 → 0
            var all = s.All();
            Assert.Single(all);
            Assert.Equal("(unknown)", all[0].Model);
            Assert.Equal(0, all[0].InputTokens);
            Assert.Equal(0, all[0].OutputTokens);
        }
        finally { File.Delete(path); }
    }
}
