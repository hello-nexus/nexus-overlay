using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelInputStateTests
{
    private static HitRegionPlan Rects(int x = 10) =>
        new(HitMode.Rects, new[] { new RegionLayout.Rect(x, 0, 5, 5) });

    [Fact]
    public void SeeThroughStartsEmptyWhetherOrNotClickThrough()
        => Assert.Equal(HitMode.Empty, new PanelInputState(true, false).Effective.Mode);

    [Fact]
    public void OpaqueKioskIsAlwaysFullWindowAndIgnoresReports()
    {
        var s = new PanelInputState(false, true);
        Assert.Equal(HitMode.Full, s.Effective.Mode);
        Assert.False(s.ApplyReport(Rects(), 0));
        Assert.False(s.ApplyReport(HitRegionPlan.FullLease, 0));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void ReportWidensRegionOnlyWhenChanged()
    {
        var s = new PanelInputState(true, false);
        Assert.True(s.ApplyReport(Rects(), 0));
        Assert.Equal(HitMode.Rects, s.Effective.Mode);
        Assert.False(s.ApplyReport(Rects(), 0));
        Assert.True(s.ApplyReport(Rects(20), 0));
        Assert.True(s.ApplyReport(HitRegionPlan.Full, 0));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void NonLeaseFullHasNoExpiry()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.Full, 0);
        Assert.False(s.Expire(1_000_000, out _));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void LeaseWidensToFullThenLapsToLastNonLeaseRegion()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(Rects(), 0);
        Assert.True(s.ApplyReport(HitRegionPlan.FullLease, 100));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
        Assert.False(s.Expire(100 + PanelInputState.LeaseMs - 1, out _));
        Assert.True(s.Expire(100 + PanelInputState.LeaseMs, out _));
        Assert.Equal(Rects(), s.Effective);
    }

    [Fact]
    public void LeaseWithNoPriorRegionLapsesToEmpty()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.FullLease, 0);
        Assert.True(s.Expire(PanelInputState.LeaseMs, out _));
        Assert.Equal(HitMode.Empty, s.Effective.Mode);
    }

    [Fact]
    public void RenewalExtendsTheLeaseWithoutChangingTheRegion()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.FullLease, 0);
        Assert.False(s.ApplyReport(HitRegionPlan.FullLease, 2_000));
        Assert.False(s.Expire(PanelInputState.LeaseMs, out _));
        Assert.True(s.Expire(2_000 + PanelInputState.LeaseMs, out _));
    }

    [Fact]
    public void LapsingLowersAnyRaise()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.FullLease, 0);
        s.SetRaised(true);
        Assert.True(s.Expire(PanelInputState.LeaseMs, out var lowered));
        Assert.True(lowered);
        Assert.False(s.Raised);
        Assert.True(s.SinkToBottom);
    }

    [Fact]
    public void NonLeaseReportEndsTheLeaseImmediately()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.FullLease, 0);
        Assert.True(s.ApplyReport(Rects(), 10));
        Assert.False(s.LeaseActive);
        Assert.Equal(HitMode.Rects, s.Effective.Mode);
    }

    [Fact]
    public void DeadPageReturnsToEmptyAndDropsLeaseAndRaise()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.FullLease, 0);
        s.SetRaised(true);
        Assert.True(s.PageLost());
        Assert.Equal(HitMode.Empty, s.Effective.Mode);
        Assert.False(s.LeaseActive);
        Assert.False(s.Raised);
        Assert.False(s.PageLost());
    }

    [Fact]
    public void DeadPageOnOpaqueKioskStaysFullWindow()
    {
        var s = new PanelInputState(false, false);
        Assert.False(s.PageLost());
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void RaiseOnlyAppliesToNonTopmost()
    {
        var top = new PanelInputState(false, true);
        Assert.False(top.SetRaised(true));
        Assert.False(top.SinkToBottom);

        var low = new PanelInputState(false, false);
        Assert.True(low.SinkToBottom);
        Assert.True(low.SetRaised(true));
        Assert.False(low.SinkToBottom);
        Assert.False(low.SetRaised(true));
        Assert.True(low.SetRaised(false));
        Assert.True(low.SinkToBottom);
    }

    [Fact]
    public void BecomingTopmostDropsRaise()
    {
        var s = new PanelInputState(false, false);
        s.SetRaised(true);
        Assert.True(s.SetTopmost(true));
        Assert.False(s.Raised);
        Assert.False(s.SinkToBottom);
        Assert.False(s.SetTopmost(true));
    }
}
