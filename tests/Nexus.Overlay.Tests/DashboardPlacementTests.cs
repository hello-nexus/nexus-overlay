using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class DashboardPlacementTests
{
    [Fact]
    public void PickTarget_PrimaryNotHidden_ChoosesPrimary()
    {
        Assert.Equal(1, DashboardPlacement.PickTarget(
            new[] { true, false, false }, new[] { false, true, false }));
    }

    [Fact]
    public void PickTarget_PrimaryHidden_ChoosesFirstOtherMonitor()
    {
        Assert.Equal(1, DashboardPlacement.PickTarget(
            new[] { true, false, false }, new[] { true, false, false }));
    }

    [Fact]
    public void PickTarget_EveryMonitorHidden_ReturnsNone()
    {
        Assert.Equal(-1, DashboardPlacement.PickTarget(
            new[] { true, true }, new[] { true, false }));
    }

    [Fact]
    public void Center_FitsWorkArea_CentersVisiblePart()
    {
        // 1000x700 window with 8px side and 8px bottom frame in a 1920x1040 work area.
        var (x, y, w, h) = DashboardPlacement.Center(1000, 700, 8, 8, 0, 0, 1920, 1040);
        Assert.Equal((1920 - 984) / 2 - 8, x);
        Assert.Equal((1040 - 692) / 2, y);
        Assert.Equal(1000, w);
        Assert.Equal(700, h);
    }

    [Fact]
    public void Center_LargerThanWorkArea_ClampsVisiblePart()
    {
        var (x, y, w, h) = DashboardPlacement.Center(3000, 2000, 8, 8, 100, 50, 1100, 650);
        Assert.Equal(100 - 8, x);
        Assert.Equal(50, y);
        Assert.Equal(1000 + 16, w);
        Assert.Equal(600 + 8, h);
    }
}
