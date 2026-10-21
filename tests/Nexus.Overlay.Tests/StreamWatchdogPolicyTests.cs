using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public class StreamWatchdogPolicyTests
{
    [Fact]
    public void Flowing_frames_never_probe_or_restart()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < 100; i++) Assert.Equal(WatchdogAction.None, p.Tick(30));
    }

    [Fact]
    public void Silent_capture_asks_for_a_probe()
    {
        var p = new StreamWatchdogPolicy();
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
    }

    [Fact]
    public void Static_page_is_probed_but_never_restarted_below_the_ceiling()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks - 1; i++)
        {
            Assert.Equal(WatchdogAction.Probe, p.Tick(0));
            Assert.False(p.OnProbe(42));
        }
    }

    [Fact]
    public void Changed_page_while_capture_is_silent_restarts_on_the_second_probe()
    {
        var p = new StreamWatchdogPolicy();
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
        Assert.False(p.OnProbe(1));
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
        Assert.True(p.OnProbe(2));
    }

    [Fact]
    public void First_probe_of_a_run_is_only_a_baseline()
    {
        var p = new StreamWatchdogPolicy();
        p.Tick(0);
        Assert.False(p.OnProbe(7));
    }

    [Fact]
    public void Stale_every_cycle_restarts_every_cycle_without_backoff()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            p.Tick(0);
            Assert.False(p.OnProbe((ulong)(cycle * 10 + 1)));
            p.Tick(0);
            Assert.True(p.OnProbe((ulong)(cycle * 10 + 2)));
            p.Tick(1); // the recreated session's initial frame
        }
    }

    [Fact]
    public void Frames_flowing_again_clears_the_baseline()
    {
        var p = new StreamWatchdogPolicy();
        p.Tick(0);
        p.OnProbe(1);
        p.Tick(30);
        p.Tick(0);
        Assert.False(p.OnProbe(2));
    }

    [Fact]
    public void Failing_probes_stop_probing_and_the_ceiling_still_recovers()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < StreamWatchdogPolicy.MaxProbeFailures; i++)
        {
            Assert.Equal(WatchdogAction.Probe, p.Tick(0));
            p.OnProbeFailed();
        }
        Assert.True(p.ProbeBroken);
        WatchdogAction last = WatchdogAction.None;
        for (var i = p.ZeroTicks; i < StreamWatchdogPolicy.CeilingZeroTicks; i++) last = p.Tick(0);
        Assert.Equal(WatchdogAction.Restart, last);
    }

    [Fact]
    public void Unchanging_preview_cannot_mask_a_stale_capture_past_the_ceiling()
    {
        var p = new StreamWatchdogPolicy();
        var action = WatchdogAction.None;
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            action = p.Tick(0);
            if (action == WatchdogAction.Probe) p.OnProbe(5);
        }
        Assert.Equal(WatchdogAction.Restart, action);
    }

    [Fact]
    public void A_successful_probe_clears_the_failure_count()
    {
        var p = new StreamWatchdogPolicy();
        p.OnProbeFailed();
        p.OnProbeFailed();
        p.Tick(0);
        p.OnProbe(1);
        p.OnProbeFailed();
        Assert.False(p.ProbeBroken);
    }
}
