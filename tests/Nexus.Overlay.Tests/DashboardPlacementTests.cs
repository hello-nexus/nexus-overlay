using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class DashboardPlacementTests
{
    private static readonly PlacementMonitor Main = new("MAIN", true, 0, 0, 1920, 1200);
    private static readonly PlacementMonitor Panel = new("PANEL", false, 1920, 22, 2653, 2582);
    private static readonly PlacementMonitor Third = new("THIRD", false, -1920, 0, 0, 1080);

    [Fact]
    public void PickTarget_WindowOnPanel_ChoosesPrimary()
    {
        Assert.Equal(0, DashboardPlacement.PickTarget(
            new[] { Main, Panel }, new[] { "PANEL" }, 1700, 200, 2900, 1700));
    }

    [Fact]
    public void PickTarget_WindowOnNonPanelMonitor_Stays()
    {
        Assert.Equal(-1, DashboardPlacement.PickTarget(
            new[] { Main, Panel }, new[] { "PANEL" }, 300, 200, 1600, 1000));
    }

    [Fact]
    public void PickTarget_PanelIsPrimary_ChoosesFirstOtherMonitor()
    {
        var primaryPanel = new PlacementMonitor("PANEL", true, 0, 0, 733, 2560);
        var other = new PlacementMonitor("MAIN", false, 733, 0, 2653, 1200);
        Assert.Equal(1, DashboardPlacement.PickTarget(
            new[] { primaryPanel, other, Third }, new[] { "PANEL" }, 100, 100, 700, 900));
    }

    [Fact]
    public void PickTarget_EveryMonitorIsPanel_Stays()
    {
        Assert.Equal(-1, DashboardPlacement.PickTarget(
            new[] { Main, Panel }, new[] { "MAIN", "PANEL" }, 1700, 200, 2900, 1700));
    }

    [Fact]
    public void PickTarget_NoOverlap_UsesNearestMonitor()
    {
        Assert.Equal(0, DashboardPlacement.PickTarget(
            new[] { Main, Panel }, new[] { "PANEL" }, 3000, 3000, 3400, 3400));
    }

    [Fact]
    public void Center_FitsWorkArea_CentersVisiblePart()
    {
        // The frame is added outside the centered visible part.
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
