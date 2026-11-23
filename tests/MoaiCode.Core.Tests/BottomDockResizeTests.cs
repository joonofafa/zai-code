using System.Reflection;
using MoaiCode.Tui;
using Xunit;

namespace MoaiCode.Core.Tests;

public sealed class BottomDockResizeTests
{
    [Fact]
    public void ResizeEraseBand_width_change_does_not_abandon_stale_composer()
    {
        var dock = new BottomDock(() => "status");
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Set(dock, flags, "_lastW", 100);
        Set(dock, flags, "_lastH", 24);
        Set(dock, flags, "_reserved", 4);
        Set(dock, flags, "_lastBoxTop", 21);
        Set(dock, flags, "_lastCursorRow", 22);
        dock.CursorRowProbeForTest = () => 22;

        var band = ((int First, int Last, int? Shift, int? CursorRow))typeof(BottomDock)
            .GetMethod("ResizeEraseBand", flags)!.Invoke(dock, new object[] { 60, 24 })!;

        Assert.NotEqual(0, band.Last);
        Assert.True(band.First <= 21, $"erase starts at {band.First}, old composer starts at 21");
        Assert.True(band.Last >= 24, $"erase ends at {band.Last}, old composer ends at 24");
    }

    private static void Set(BottomDock dock, BindingFlags flags, string name, object value)
        => typeof(BottomDock).GetField(name, flags)!.SetValue(dock, value);
}
