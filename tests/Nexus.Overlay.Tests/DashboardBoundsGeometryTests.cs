using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class DashboardBoundsGeometryTests
{
    // Three-monitor layout: primary at the origin, one to the left, one to the right.
    private static readonly (int, int, int, int)[] Works =
    {
        (0, 0, 2560, 1400),
        (-1920, 0, 0, 1040),
        (2560, 0, 4480, 1040),
    };

    private const int Caption = 32, MinW = 200, MinH = 16;

    [Fact]
    public void RectOnPrimary_Reachable()
    {
        Assert.True(DashboardBoundsGeometry.StripReachable(400, 200, 1700, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void RectOnLeftMonitor_Reachable()
    {
        Assert.True(DashboardBoundsGeometry.StripReachable(-1800, 100, -500, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void RectSavedWhilePanelWasPrimary_NotReachable()
    {
        // Customer rect from a session where a case display was the primary.
        Assert.False(DashboardBoundsGeometry.StripReachable(-3269, 156, -3269 + 1362, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void StripBelowEveryWorkArea_NotReachable()
    {
        Assert.False(DashboardBoundsGeometry.StripReachable(400, 1395, 1700, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void StripAboveTopEdge_NotReachable()
    {
        Assert.False(DashboardBoundsGeometry.StripReachable(400, -40, 1700, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void SliverOnEdge_NotReachable()
    {
        // Only 100px of the title bar pokes onto the right monitor.
        Assert.False(DashboardBoundsGeometry.StripReachable(4380, 100, 5680, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void MostlyOffButEnoughOnScreen_Reachable()
    {
        Assert.True(DashboardBoundsGeometry.StripReachable(4180, 100, 5480, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void NoMonitors_NotReachable()
    {
        Assert.False(DashboardBoundsGeometry.StripReachable(0, 0, 1000, Caption, System.Array.Empty<(int, int, int, int)>(), MinW, MinH));
    }
}
