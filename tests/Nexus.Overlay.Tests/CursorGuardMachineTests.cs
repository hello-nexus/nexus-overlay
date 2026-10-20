using System.Collections.Generic;
using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

/// <summary>Replays hook and cursor-move event sequences through the guard's decision machine.</summary>
public class CursorGuardMachineTests
{
    private sealed class World : ICursorGuardWorld
    {
        public List<(int Left, int Top, int Right, int Bottom)> MonitorList { get; } = new();
        public List<(int Left, int Top, int Right, int Bottom)> KioskList { get; } = new();
        public List<(int Left, int Top, int Right, int Bottom)> KeepOffList { get; } = new();

        public IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Monitors => MonitorList;
        public IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Kiosks => KioskList;
        public bool HasKeepOff => KeepOffList.Count > 0;

        public bool OnKeepOffPanel((int X, int Y) p)
        {
            foreach (var r in KeepOffList)
            {
                if (p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom) return true;
            }
            return false;
        }
    }

    // A portrait kiosk right of the primary and offset a little lower, so a narrow gap
    // sits above it and the two share the rest of the primary's right edge.
    private static readonly (int Left, int Top, int Right, int Bottom) Primary = (0, 0, 1920, 1200);
    private static readonly (int Left, int Top, int Right, int Bottom) Panel = (1920, 22, 3020, 3862);

    private static World SingleKiosk(bool keepOff = false)
    {
        var world = new World();
        world.MonitorList.Add(Primary);
        world.MonitorList.Add(Panel);
        world.KioskList.Add(Panel);
        if (keepOff) world.KeepOffList.Add(Panel);
        return world;
    }

    private static GuardHookResult Hook(CursorGuardMachine m, (int X, int Y) raw, (int X, int Y) cursor, long t,
        bool touchSignature = false) => m.Hook(raw, cursor, isMouseMessage: true, isMouseMove: !touchSignature, t);

    [Fact]
    public void HookThenCursorMove_SlideThroughTheGapIsTheMouses()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1914, 10));
        Assert.False(Hook(m, (1925, 10), (1914, 10), 1000).Block);
        var result = m.CursorMoved((1923, 22), 1010);
        Assert.Equal(GuardEventKind.EntryAccepted, result.Kind);
        Assert.True(m.MouseOnKiosk);
    }

    [Fact]
    public void CursorMoveThenHook_NothingIsUndone()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1914, 10));
        Hook(m, (1925, 10), (1914, 10), 1000);
        Assert.Equal(GuardEventKind.EntryAccepted, m.CursorMoved((1923, 22), 1010).Kind);
        Hook(m, (1935, 10), (1923, 22), 1015);
        Assert.Equal(GuardEventKind.None, m.CursorMoved((1930, 22), 1020).Kind);
        Assert.True(m.MouseOnKiosk);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void SeveralHooksBeforeTheCursorMoveCallback_TheEntryStillStands(int hooks)
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1914, 10));
        Hook(m, (1925, 10), (1914, 10), 1000);
        var accepted = Hook(m, (1932, 10), (1923, 22), 1004);
        Assert.True(accepted.EntryAccepted);
        for (var i = 2; i < hooks; i++) Assert.False(Hook(m, (1940, 10), (1930, 22), 1004 + i * 2).Block);
        Assert.Equal(GuardEventKind.None, m.CursorMoved((1930, 22), 1015).Kind);
        Assert.True(m.MouseOnKiosk);
    }

    [Fact]
    public void DiagonalUpEntryThroughTheGap_IsTheMouses()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1915, 15));
        Hook(m, (1930, 5), (1915, 15), 1000);
        Assert.Equal(GuardEventKind.EntryAccepted, m.CursorMoved((1926, 22), 1008).Kind);
        Assert.True(m.MouseOnKiosk);
    }

    [Fact]
    public void TouchTeleportOntoTheKioskWithNoPush_IsReturnedToTheMouse()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1000, 500));
        Hook(m, (1005, 500), (1000, 500), 1000);
        var result = m.CursorMoved((2300, 1500), 1500);
        Assert.Equal(GuardEventKind.MoveCursor, result.Kind);
        Assert.Equal((1005, 500), result.Target);
        Assert.Equal("touch", result.Reason);
    }

    [Theory]
    [InlineData(2300, 1500)]
    [InlineData(1925, 60)]
    public void TouchTeleportDuringAPushAtTheEdge_IsStillReturned(int x, int y)
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1900, 0));
        Hook(m, (1900, -10), (1900, 0), 1000);
        var result = m.CursorMoved((x, y), 1020);
        Assert.Equal(GuardEventKind.MoveCursor, result.Kind);
        Assert.Equal((1900, 0), result.Target);
        Assert.Equal("gap entry rejected, treated as touch", result.Reason);
        Assert.False(m.MouseOnKiosk);
    }

    [Fact]
    public void MouseRestingOnTheKioskPushingOffItsOuterEdge_StaysThere()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((3000, 2000));
        Assert.True(m.MouseOnKiosk);
        Assert.False(Hook(m, (3030, 2000), (3010, 2000), 1000).Block);
        Assert.Equal(GuardEventKind.None, m.CursorMoved((3019, 2100), 1010).Kind);
        Assert.False(Hook(m, (3040, 2100), (3019, 2100), 1012).Block);
        Assert.Equal(GuardEventKind.None, m.CursorMoved((3019, 2200), 1020).Kind);
        Assert.True(m.MouseOnKiosk);
    }

    [Fact]
    public void TouchPlacedCursorWithAMousePushTowardTheGap_IsStillReturned()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1000, 500));
        Hook(m, (1005, 500), (1000, 500), 1000);
        Assert.False(Hook(m, (1925, 12), (1925, 30), 1500).Block);
        Assert.False(Hook(m, (1925, 5), (1925, 30), 1504).Block);
        var result = m.CursorMoved((1925, 30), 1510);
        Assert.Equal(GuardEventKind.MoveCursor, result.Kind);
        Assert.Equal((1005, 500), result.Target);
        Assert.Equal("gap entry rejected, treated as touch", result.Reason);
    }

    [Fact]
    public void CrossingBetweenTwoKiosks_IsTheMouses()
    {
        var world = new World();
        var q60 = (Left: 2000, Top: 0, Right: 2720, Bottom: 1280);
        var y70 = (Left: 2900, Top: 0, Right: 3600, Bottom: 3840);
        world.MonitorList.AddRange(new[] { Primary, q60, y70 });
        world.KioskList.AddRange(new[] { q60, y70 });
        var m = new CursorGuardMachine(world);
        m.Start((2700, 500));
        Assert.True(m.MouseOnKiosk);
        Hook(m, (2800, 500), (2719, 500), 1000);
        Assert.Equal(GuardEventKind.EntryAccepted, m.CursorMoved((2900, 500), 1008).Kind);
        Assert.True(m.MouseOnKiosk);
    }

    [Fact]
    public void KeepOff_TheHookWallsTheMouseOutOfThePanel()
    {
        var m = new CursorGuardMachine(SingleKiosk(keepOff: true));
        m.Start((1914, 300));
        Assert.True(Hook(m, (1930, 300), (1915, 300), 1000).Block);
    }

    [Fact]
    public void KeepOff_ASlideThroughTheGapIsMovedOffNotAccepted()
    {
        var m = new CursorGuardMachine(SingleKiosk(keepOff: true));
        m.Start((1914, 10));
        Hook(m, (1925, 10), (1914, 10), 1000);
        var result = m.CursorMoved((1923, 22), 1010);
        Assert.Equal(GuardEventKind.MoveCursor, result.Kind);
        Assert.Equal("keep-off", result.Reason);
    }

    [Fact]
    public void ExpiredPush_TheSlideIsTreatedAsTouch()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1914, 10));
        Hook(m, (1925, 10), (1914, 10), 1000);
        var result = m.CursorMoved((1923, 22), 1000 + MouseEntryTracker.WindowMs + 1);
        Assert.Equal(GuardEventKind.MoveCursor, result.Kind);
        Assert.Equal("touch", result.Reason);
        Assert.False(m.EntryArmed);
    }

    [Fact]
    public void ASecondPushDoesNotExtendAPendingOnesWindow()
    {
        var m = new CursorGuardMachine(SingleKiosk());
        m.Start((1900, 0));
        Hook(m, (1900, -10), (1900, 0), 1000);
        Hook(m, (1900, -10), (1900, 0), 1090);
        var result = m.CursorMoved((2300, 1500), 1150);
        Assert.Equal("touch", result.Reason);
    }
}
