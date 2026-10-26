using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelInputStateTests
{
    private static HitRegionPlan Rects(int x = 10) =>
        new(HitMode.Rects, new[] { new RegionLayout.Rect(x, 0, 5, 5) });

    [Fact]
    public void ClickThroughStartsEmpty()
        => Assert.Equal(HitMode.Empty, new PanelInputState(true, false).Effective.Mode);

    [Fact]
    public void NonClickThroughIsAlwaysFullWindow()
    {
        var s = new PanelInputState(false, true);
        Assert.False(s.ApplyReport(Rects()));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void ReportWidensRegionOnlyWhenChanged()
    {
        var s = new PanelInputState(true, false);
        Assert.True(s.ApplyReport(Rects()));
        Assert.Equal(HitMode.Rects, s.Effective.Mode);
        Assert.False(s.ApplyReport(Rects()));
        Assert.True(s.ApplyReport(Rects(20)));
        Assert.True(s.ApplyReport(HitRegionPlan.Full));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void DeadPageReturnsToEmptyRegion()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(HitRegionPlan.Full);
        Assert.True(s.PageLost());
        Assert.Equal(HitMode.Empty, s.Effective.Mode);
        Assert.False(s.PageLost());
    }

    [Fact]
    public void DeadPageLowersARaisedKiosk()
    {
        var s = new PanelInputState(true, false);
        s.SetRaised(true);
        Assert.True(s.PageLost());
        Assert.False(s.Raised);
        Assert.True(s.SinkToBottom);
    }

    [Fact]
    public void DeadPageOnNonClickThroughStaysFullWindowButClearsRaise()
    {
        var s = new PanelInputState(false, false);
        Assert.False(s.PageLost());
        Assert.Equal(HitMode.Full, s.Effective.Mode);
    }

    [Fact]
    public void TogglingClickThroughOnRearmsEmpty()
    {
        var s = new PanelInputState(true, false);
        s.ApplyReport(Rects());
        Assert.True(s.SetClickThrough(false));
        Assert.Equal(HitMode.Full, s.Effective.Mode);
        Assert.True(s.SetClickThrough(true));
        Assert.Equal(HitMode.Empty, s.Effective.Mode);
        Assert.False(s.SetClickThrough(true));
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
