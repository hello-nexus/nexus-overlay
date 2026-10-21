using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public class StreamWatchdogPolicyTests
{
    private static bool TickZeros(StreamWatchdogPolicy p, int count)
    {
        var fired = false;
        for (var i = 0; i < count; i++) fired |= p.Tick(0);
        return fired;
    }

    [Fact]
    public void Flowing_frames_never_restart()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < 100; i++) Assert.False(p.Tick(30));
    }

    [Fact]
    public void Restarts_after_two_zero_ticks()
    {
        var p = new StreamWatchdogPolicy();
        Assert.False(p.Tick(0));
        Assert.True(p.Tick(0));
    }

    [Fact]
    public void Static_page_backs_off_instead_of_looping()
    {
        var p = new StreamWatchdogPolicy();
        var restarts = 0;
        // Each restart yields exactly one initial frame, then silence.
        for (var cycle = 0; cycle < 200; cycle++)
        {
            if (p.Tick(0)) { restarts++; p.Tick(1); }
        }
        Assert.True(restarts < 10, $"restarts={restarts}");
        Assert.Equal(StreamWatchdogPolicy.MaxZeroTicks, p.RequiredZeroTicks);
    }

    [Fact]
    public void Productive_restart_resets_backoff()
    {
        var p = new StreamWatchdogPolicy();
        Assert.True(TickZeros(p, 2));
        p.Tick(1);
        Assert.True(TickZeros(p, 2));
        Assert.Equal(4, p.RequiredZeroTicks);
        p.Tick(1);
        p.Tick(30);
        Assert.Equal(StreamWatchdogPolicy.BaseZeroTicks, p.RequiredZeroTicks);
        Assert.True(TickZeros(p, 2));
    }

    [Fact]
    public void Dead_capture_keeps_retrying_with_backoff()
    {
        var p = new StreamWatchdogPolicy();
        Assert.True(TickZeros(p, 2));
        Assert.True(TickZeros(p, 2));
        Assert.False(TickZeros(p, 3));
        Assert.True(p.Tick(0));
    }
}
