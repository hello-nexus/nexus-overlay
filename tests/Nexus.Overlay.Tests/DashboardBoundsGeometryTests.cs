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
        // Less than the minimum width of the title bar overlaps the right monitor.
        Assert.False(DashboardBoundsGeometry.StripReachable(4380, 100, 5680, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void MostlyOffButEnoughOnScreen_Reachable()
    {
        Assert.True(DashboardBoundsGeometry.StripReachable(4180, 100, 5480, Caption, Works, MinW, MinH));
    }

    [Fact]
    public void UsableMonitors_SkipsPanels()
    {
        Assert.Equal(new[] { 0, 2 }, DashboardBoundsGeometry.UsableMonitors(new[] { false, true, false }));
    }

    [Fact]
    public void UsableMonitors_AllPanels_KeepsAll()
    {
        Assert.Equal(new[] { 0, 1 }, DashboardBoundsGeometry.UsableMonitors(new[] { true, true }));
    }

    [Fact]
    public void DefaultMonitor_PrimaryNotPanel_IsPrimary()
    {
        Assert.Equal(1, DashboardBoundsGeometry.DefaultMonitor(new[] { (false, false), (true, false), (false, true) }));
    }

    [Fact]
    public void DefaultMonitor_PanelIsPrimary_FirstNonPanel()
    {
        // A case display briefly made the OS primary.
        Assert.Equal(1, DashboardBoundsGeometry.DefaultMonitor(new[] { (true, true), (false, false), (false, false) }));
    }

    [Fact]
    public void DefaultMonitor_OnlyPanels_IsPrimary()
    {
        Assert.Equal(1, DashboardBoundsGeometry.DefaultMonitor(new[] { (false, true), (true, true) }));
    }

    [Fact]
    public void DefaultMonitor_None_IsMinusOne()
    {
        Assert.Equal(-1, DashboardBoundsGeometry.DefaultMonitor(System.Array.Empty<(bool, bool)>()));
    }

    [Fact]
    public void WorkspaceToScreen_TopTaskbar_ShiftsDownByTaskbar()
    {
        // Monitor at the origin with a top-docked taskbar: the work area starts below it.
        Assert.Equal((100, 248), DashboardBoundsGeometry.WorkspaceToScreen(100, 200, 0, 0, 0, 48));
    }

    [Fact]
    public void WorkspaceToScreen_SecondaryLeftTaskbar_ShiftsRightByTaskbar()
    {
        Assert.Equal((-1800, 50), DashboardBoundsGeometry.WorkspaceToScreen(-1860, 50, -1920, 0, -1860, 0));
    }

    [Fact]
    public void WorkspaceToScreen_BottomTaskbar_Unchanged()
    {
        Assert.Equal((300, 400), DashboardBoundsGeometry.WorkspaceToScreen(300, 400, 0, 0, 0, 0));
    }

    [Fact]
    public void NoMonitors_NotReachable()
    {
        Assert.False(DashboardBoundsGeometry.StripReachable(0, 0, 1000, Caption, System.Array.Empty<(int, int, int, int)>(), MinW, MinH));
    }
}
