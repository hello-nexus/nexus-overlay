using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class CursorGuardGeometryTests
{
    // A portrait Y70 kiosk left of the primary whose bottom edge sits partway
    // down the primary's left edge, so the two share only the upper part of it.
    private static readonly (int Left, int Top, int Right, int Bottom) Primary = (0, 0, 2560, 1440);
    private static readonly (int Left, int Top, int Right, int Bottom) Y70 = (-682, -2104, 0, 456);
    private static readonly (int Left, int Top, int Right, int Bottom)[] Monitors = { Primary, Y70 };

    private static bool OnY70((int X, int Y) p) =>
        p.X >= Y70.Left && p.X < Y70.Right && p.Y >= Y70.Top && p.Y < Y70.Bottom;

    [Fact]
    public void ClampToCursorMonitor_PointOnMonitor_Unchanged()
    {
        Assert.Equal((1200, 700), CursorGuardGeometry.ClampToCursorMonitor((1200, 700), (1190, 700), Monitors));
        Assert.Equal((-300, -1000), CursorGuardGeometry.ClampToCursorMonitor((-300, -1000), (1, 400), Monitors));
    }

    [Fact]
    public void ClampToCursorMonitor_OffsetPushBelowPanel_StopsAtPrimaryLeftEdge()
    {
        Assert.Equal((0, 800), CursorGuardGeometry.ClampToCursorMonitor((-3, 800), (0, 800), Monitors));
    }

    [Fact]
    public void ClampToCursorMonitor_PushJustBelowPanelCorner_StaysOnPrimary()
    {
        // The panel's corner is nearer to this point than the primary edge is.
        var p = CursorGuardGeometry.ClampToCursorMonitor((-20, 465), (0, 470), Monitors);
        Assert.Equal((0, 465), p);
        Assert.False(OnY70(p));
    }

    [Fact]
    public void ClampToCursorMonitor_DiagonalSlideBelowPanelCorner_NeverLandsOnPanel()
    {
        var cursor = (X: 0, Y: Y70.Bottom);
        for (var step = 0; step < 20; step++)
        {
            var next = CursorGuardGeometry.ClampToCursorMonitor((cursor.X - 4, cursor.Y + 1), cursor, Monitors);
            Assert.False(OnY70(next));
            Assert.Equal((0, cursor.Y + 1), next);
            cursor = next;
        }
    }

    [Fact]
    public void ClampToCursorMonitor_OuterEdgePushOffPanel_StopsAtPanelLeftEdge()
    {
        Assert.Equal((-682, -1000), CursorGuardGeometry.ClampToCursorMonitor((-690, -1000), (-682, -1000), Monitors));
    }

    [Fact]
    public void ClampToCursorMonitor_PushOffPanelBottom_StaysOnPanel()
    {
        Assert.Equal((-300, 455), CursorGuardGeometry.ClampToCursorMonitor((-300, 458), (-300, 455), Monitors));
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
        // The hook point past the primary's left edge, recorded clamped onto the primary.
        var origin = CursorGuardGeometry.ClampToCursorMonitor((-3, 800), (0, 800), Monitors);
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
        var recorded = CursorGuardGeometry.ClampToCursorMonitor((-690, -1000), (-682, -1000), Monitors);
        Assert.True(OnY70(recorded));
        Assert.False(CursorGuardGeometry.MustLeave(onKiosk: true, onKeepOff: false, mouseOnPanel: OnY70(recorded)));
    }

    [Theory]
    [InlineData(0xFF515700u, true)]
    [InlineData(0xFF5157FFu, true)]
    [InlineData(0u, false)]
    [InlineData(0xFF515600u, false)]
    public void IsTouchOrPen_MatchesTheTouchSignature(uint extraInfo, bool expected) =>
        Assert.Equal(expected, CursorGuardGeometry.IsTouchOrPen(extraInfo));

    // The lab Y70: a portrait kiosk right of the primary, 22 px lower, so the
    // two share the primary's whole right edge (primary is 0,0,1920,1200) and a 22 px gap sits above the kiosk.
    private static readonly (int Left, int Top, int Right, int Bottom) YRight = (1920, 22, 3020, 3862);
    private static readonly (int Left, int Top, int Right, int Bottom)[] YRightKiosks = { YRight };

    private static MouseEntryVerdict Judge(ref MouseEntryTracker tracker, (int X, int Y) now, long atMs,
        (int Left, int Top, int Right, int Bottom)[]? kiosks = null) =>
        tracker.Judge(now, kiosks ?? YRightKiosks, atMs, out _);

    [Fact]
    public void MouseEntry_NothingArmed_IsNone()
    {
        var tracker = new MouseEntryTracker();
        Assert.Equal(MouseEntryVerdict.None, Judge(ref tracker, (1923, 22), 50));
    }

    [Fact]
    public void MouseEntry_PushIntoTheGapAboveTheKiosk_SlideOntoItIsAccepted()
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        var verdict = tracker.Judge((1923, 22), YRightKiosks, 1010, out var slide);
        Assert.Equal(MouseEntryVerdict.Accepted, verdict);
        Assert.Equal(2, slide);
    }

    [Fact]
    public void MouseEntry_JudgedOnce_SecondCallSeesNothing()
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        Assert.Equal(MouseEntryVerdict.Accepted, Judge(ref tracker, (1923, 22), 1010));
        Assert.Equal(MouseEntryVerdict.None, Judge(ref tracker, (1923, 22), 1011));
    }

    [Fact]
    public void MouseEntry_ArmedStateSurvivesUntilJudged_RegardlessOfOrdering()
    {
        // A later hook that is not a push neither disarms nor re-arms: the
        // callback may judge at any time inside the window.
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        Assert.True(tracker.Armed);
        Assert.Equal(MouseEntryVerdict.Accepted, Judge(ref tracker, (1923, 22), 1000 + MouseEntryTracker.WindowMs));
    }

    [Fact]
    public void MouseEntry_HookThatSawTheLandingDisarms_CallbackThenSeesNothing()
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        tracker.Disarm();
        Assert.Equal(MouseEntryVerdict.None, Judge(ref tracker, (1923, 22), 1010));
    }

    [Fact]
    public void MouseEntry_Expired_IsNoneAndDisarms()
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        Assert.Equal(MouseEntryVerdict.None, Judge(ref tracker, (1923, 22), 1000 + MouseEntryTracker.WindowMs + 1));
        Assert.False(tracker.Armed);
    }

    [Fact]
    public void MouseEntry_CursorOnNoKiosk_IsNoneAndStaysArmed()
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        Assert.Equal(MouseEntryVerdict.None, Judge(ref tracker, (1500, 300), 1010));
        Assert.True(tracker.Armed);
    }

    [Fact]
    public void MouseEntry_TouchTapFarFromTheEdgePushing_IsRejected()
    {
        // The mouse pushes at the gap while a finger taps the middle of the kiosk.
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1914, 10), 1000);
        Assert.Equal(MouseEntryVerdict.Rejected, Judge(ref tracker, (2400, 1800), 1010));
    }

    [Fact]
    public void MouseEntry_TouchNearTheEdgeWithoutAPush_IsNotAnEntry()
    {
        // A parked or jittering mouse never arms the tracker, so a tap beside it stays a teleport.
        var tracker = new MouseEntryTracker();
        Assert.Equal(MouseEntryVerdict.None, Judge(ref tracker, (1930, 300), 1010));
    }

    [Fact]
    public void MouseEntry_LandingAgainstThePushDirection_IsRejected()
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1915, 2000), (1925, 2000), 1000);
        Assert.Equal(MouseEntryVerdict.Rejected, Judge(ref tracker, (1930, 2000), 1010));
    }

    [Theory]
    [InlineData(MouseEntryTracker.SlideTolerancePx, true)]
    [InlineData(MouseEntryTracker.SlideTolerancePx + 1, false)]
    public void MouseEntry_SlideAtTheToleranceBoundary(int slide, bool accepted)
    {
        var tracker = new MouseEntryTracker();
        tracker.Arm((1925, 10), (1925, 0), 1000);
        Assert.Equal(accepted ? MouseEntryVerdict.Accepted : MouseEntryVerdict.Rejected,
            Judge(ref tracker, (1925, 22 + slide), 1010));
    }

    [Fact]
    public void MouseEntry_CrossingTheGapBetweenTwoKiosks_IsAccepted()
    {
        var q60 = (Left: 2000, Top: 0, Right: 2720, Bottom: 1280);
        var y70 = (Left: 2900, Top: 0, Right: 3600, Bottom: 3840);
        var tracker = new MouseEntryTracker();
        tracker.Arm((2800, 500), (2719, 500), 1000);
        Assert.Equal(MouseEntryVerdict.Accepted, Judge(ref tracker, (2900, 500), 1010, new[] { q60, y70 }));
    }
}
