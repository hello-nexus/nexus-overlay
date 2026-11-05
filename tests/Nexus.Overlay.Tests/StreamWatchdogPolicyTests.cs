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
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void Silent_capture_probes_once_per_interval()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 1; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            var action = p.Tick(0);
            Assert.Equal(i % StreamWatchdogPolicy.ProbeEveryZeroTicks == 0 ? WatchdogAction.Probe : WatchdogAction.None, action);
        }
    }

    [Fact]
    public void Static_page_whose_probe_repaint_is_captured_never_restarts()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < 500; cycle++)
        {
            Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
            p.OnProbe();
            Assert.Equal(WatchdogAction.None, p.Tick(1)); // the repaint's WGC frame
        }
    }

    [Fact]
    public void Probe_repaint_with_no_frame_restarts_after_one_full_tick()
    {
        var p = new StreamWatchdogPolicy();
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
        p.OnProbe();
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Restart, p.Tick(0));
        Assert.Equal(0, p.ZeroTicks);
    }

    [Fact]
    public void Frozen_every_cycle_restarts_every_cycle_without_backoff()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
            p.OnProbe();
            p.Tick(0);
            Assert.Equal(WatchdogAction.Restart, p.Tick(0));
            p.Tick(1); // the recreated session's initial frame
        }
    }

    [Fact]
    public void Frames_after_a_probe_clear_it()
    {
        var p = new StreamWatchdogPolicy();
        TickToNextProbe(p);
        p.OnProbe();
        p.Tick(1);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void An_unanswered_probe_never_restarts_before_the_ceiling()
    {
        var p = new StreamWatchdogPolicy();
        TickToNextProbe(p);
        for (var i = p.ZeroTicks + 1; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
            Assert.NotEqual(WatchdogAction.Restart, p.Tick(0));
        Assert.Equal(WatchdogAction.Restart, p.Tick(0));
    }

    [Fact]
    public void Failing_probes_stop_probing_and_the_ceiling_still_recovers()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < StreamWatchdogPolicy.MaxProbeFailures; i++) p.OnProbeFailed();
        Assert.True(p.ProbeBroken);
        var last = WatchdogAction.None;
        for (var i = p.ZeroTicks; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            last = p.Tick(0);
            Assert.NotEqual(WatchdogAction.Probe, last);
        }
        Assert.Equal(WatchdogAction.Restart, last);
    }

    [Fact]
    public void A_successful_probe_clears_the_failure_count()
    {
        var p = new StreamWatchdogPolicy();
        p.OnProbeFailed();
        p.OnProbeFailed();
        TickToNextProbe(p);
        p.OnProbe();
        p.OnProbeFailed();
        Assert.False(p.ProbeBroken);
    }

    [Fact]
    public void Probing_resumes_after_frames_flow_again()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < StreamWatchdogPolicy.MaxProbeFailures; i++) p.OnProbeFailed();
        Assert.True(p.ProbeBroken);
        p.Tick(1);
        Assert.False(p.ProbeBroken);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void Probing_resumes_after_a_ceiling_restart()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < StreamWatchdogPolicy.MaxProbeFailures; i++) p.OnProbeFailed();
        var action = WatchdogAction.None;
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks; i++) action = p.Tick(0);
        Assert.Equal(WatchdogAction.Restart, action);
        Assert.False(p.ProbeBroken);
    }

    private static WatchdogAction TickToNextProbe(StreamWatchdogPolicy p)
    {
        for (var i = 0; i < StreamWatchdogPolicy.ProbeEveryZeroTicks; i++)
        {
            var action = p.Tick(0);
            if (action != WatchdogAction.None) return action;
        }
        return WatchdogAction.None;
    }
}
