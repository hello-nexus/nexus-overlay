using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class CursorGuardGeometryTests
{
    // A 682x2560 Y70 kiosk left of a 2560x1440 primary, bottom edge at y=456,
    // so only the primary's left edge above y=456 is shared with it.
    private static readonly (int Left, int Top, int Right, int Bottom) Primary = (0, 0, 2560, 1440);
    private static readonly (int Left, int Top, int Right, int Bottom) Y70 = (-682, -2104, 0, 456);
    private static readonly (int Left, int Top, int Right, int Bottom)[] Monitors = { Primary, Y70 };

    private static bool OnY70((int X, int Y) p) =>
        p.X >= Y70.Left && p.X < Y70.Right && p.Y >= Y70.Top && p.Y < Y70.Bottom;

    [Fact]
    public void ClampToNearest_PointOnMonitor_Unchanged()
    {
        Assert.Equal((1200, 700), CursorGuardGeometry.ClampToNearest((1200, 700), Monitors));
        Assert.Equal((-300, -1000), CursorGuardGeometry.ClampToNearest((-300, -1000), Monitors));
    }

    [Fact]
    public void ClampToNearest_OffsetPushBelowPanel_StopsAtPrimaryLeftEdge()
    {
        Assert.Equal((0, 800), CursorGuardGeometry.ClampToNearest((-3, 800), Monitors));
    }

    [Fact]
    public void ClampToNearest_OuterEdgePushOffPanel_StopsAtPanelLeftEdge()
    {
        Assert.Equal((-682, -1000), CursorGuardGeometry.ClampToNearest((-690, -1000), Monitors));
    }

    [Fact]
    public void ClampToNearest_PushOffPanelBottom_StaysOnPanel()
    {
        Assert.Equal((-300, 455), CursorGuardGeometry.ClampToNearest((-300, 458), Monitors));
    }

    [Fact]
    public void ReturnTarget_TouchReturn_GoesBackToMouse()
    {
        var target = CursorGuardGeometry.ReturnTarget((1200, 700), (-300, -1000), Monitors, OnY70);
        Assert.Equal((1200, 700), target);
    }

    [Fact]
    public void ReturnTarget_EasedJumpOntoKeptOffPanel_ReturnsToClampedEdgePoint()
    {
        // The hook reported (-3,800); the guard recorded it clamped to the primary.
        var origin = CursorGuardGeometry.ClampToNearest((-3, 800), Monitors);
        var target = CursorGuardGeometry.ReturnTarget(origin, (-1, 455), Monitors, OnY70);
        Assert.Equal((0, 800), target);
    }

    [Fact]
    public void ReturnTarget_LastMouseOnPanel_NearestPrimaryPointNotCenter()
    {
        Assert.Equal((0, 300), CursorGuardGeometry.ReturnTarget((-5, 300), (-5, 300), Monitors, OnY70));
        Assert.Equal((0, 0), CursorGuardGeometry.ReturnTarget((-300, -1000), (-300, -1000), Monitors, OnY70));
    }

    [Fact]
    public void ReturnTarget_OriginOffEveryMonitor_NearestPrimaryPoint()
    {
        Assert.Equal((0, 455), CursorGuardGeometry.ReturnTarget((-3, 800), (-1, 455), Monitors, OnY70));
    }

    [Fact]
    public void ReturnTarget_OriginOnCursorMonitor_NotUsed()
    {
        // Stale kiosk bounds: the origin is off the kiosk rect but on the cursor's monitor.
        var stale = new[] { Primary, Y70 };
        var target = CursorGuardGeometry.ReturnTarget((-100, 0), (-50, 0), stale, _ => false);
        Assert.Equal((0, 0), target);
    }

    [Fact]
    public void ReturnTarget_NoOtherMonitor_Null()
    {
        Assert.Null(CursorGuardGeometry.ReturnTarget((1200, 700), (-300, -1000), new[] { Y70 }, OnY70));
    }

    [Fact]
    public void ReturnTarget_PicksNearestOfSeveralMonitors()
    {
        var leftOfPanel = (-2602, -1000, -682, 80);
        var monitors = new[] { Primary, Y70, leftOfPanel };
        Assert.Equal((-683, -1000), CursorGuardGeometry.ReturnTarget((-600, -2000), (-600, -2000), monitors, OnY70));
    }

    [Theory]
    // keep-off off: the mouse may sit on the panel; a touch teleport (mouse elsewhere) leaves.
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    // keep-off on: the cursor leaves the panel whether the mouse or a touch put it there.
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, true)]
    // Off every kiosk the guard never moves the cursor.
    [InlineData(false, false, false, false)]
    public void MustLeave_KeepOffOnAndOff(bool onKiosk, bool onKeepOff, bool mouseOnPanel, bool expected)
    {
        Assert.Equal(expected, CursorGuardGeometry.MustLeave(onKiosk, onKeepOff, mouseOnPanel));
    }

    [Fact]
    public void OuterEdgePush_KeepOffOff_MouseStaysOnPanel()
    {
        var recorded = CursorGuardGeometry.ClampToNearest((-690, -1000), Monitors);
        Assert.True(OnY70(recorded));
        Assert.False(CursorGuardGeometry.MustLeave(onKiosk: true, onKeepOff: false, mouseOnPanel: OnY70(recorded)));
    }
}
