using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelGuardGeometryTests
{
    // Fallback work area: a 1920x1040 primary (taskbar at the bottom) at the origin.
    private const int WorkLeft = 0, WorkTop = 0, WorkRight = 1920, WorkBottom = 1040;

    [Fact]
    public void ClampTopLeft_WindowFromMonitorToTheRight_PinsToRightEdge()
    {
        // A 800x600 window sitting on a panel to the right (x=2200) of the
        // fallback. Clamp pulls it just inside the right edge, same side it came from.
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            2200, 100, 800, 600, WorkLeft, WorkTop, WorkRight, WorkBottom);
        Assert.Equal(1920 - 800, left);
        Assert.Equal(100, top);
    }

    [Fact]
    public void ClampTopLeft_WindowFromMonitorToTheLeft_PinsToLeftEdge()
    {
        // Panel to the left at negative coords.
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            -1500, 200, 800, 600, WorkLeft, WorkTop, WorkRight, WorkBottom);
        Assert.Equal(0, left);
        Assert.Equal(200, top);
    }

    [Fact]
    public void ClampTopLeft_AlreadyInside_LeftUnchanged()
    {
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            300, 250, 400, 300, WorkLeft, WorkTop, WorkRight, WorkBottom);
        Assert.Equal(300, left);
        Assert.Equal(250, top);
    }

    [Fact]
    public void ClampTopLeft_TallWindowClampedSoBottomStaysInside()
    {
        // 400x1000 window dropped near the bottom; top is pulled up so the
        // 1000px height fits within the 1040px work height.
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            500, 900, 400, 1000, WorkLeft, WorkTop, WorkRight, WorkBottom);
        Assert.Equal(500, left);
        Assert.Equal(1040 - 1000, top);
    }

    [Fact]
    public void ClampTopLeft_WindowWiderThanWorkArea_PinsToOrigin()
    {
        // 2200px wide window can't fit in 1920px; pin to the left edge instead
        // of producing a negative/over-clamped coordinate.
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            3000, 50, 2200, 500, WorkLeft, WorkTop, WorkRight, WorkBottom);
        Assert.Equal(0, left);
        Assert.Equal(50, top);
    }

    [Fact]
    public void ClampTopLeft_NonZeroOriginWorkArea_RespectsOffset()
    {
        // Fallback work area offset (e.g. taskbar on the left: x starts at 80).
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            5000, 5000, 300, 300, 80, 40, 1620, 940);
        Assert.Equal(1620 - 300, left);
        Assert.Equal(940 - 300, top);
    }
}
