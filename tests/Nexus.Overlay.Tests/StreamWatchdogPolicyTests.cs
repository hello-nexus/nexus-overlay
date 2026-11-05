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
        Assert.True(p.ProbeInFlight);
    }

    [Fact]
    public void No_second_probe_while_one_is_in_flight()
    {
        var p = new StreamWatchdogPolicy();
        TickToNextProbe(p);
        for (var i = 0; i < StreamWatchdogPolicy.ProbeTimeoutTicks - 1; i++)
            Assert.Equal(WatchdogAction.None, p.Tick(0));
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
        Assert.False(p.ProbeInFlight);
    }

    [Fact]
    public void Late_answer_after_frames_already_arrived_is_ignored()
    {
        var p = new StreamWatchdogPolicy();
        TickToNextProbe(p);
        p.Tick(1); // the repaint's frame lands before the completion
        Assert.False(p.ProbeInFlight);
        p.OnProbe();
        for (var i = 1; i < StreamWatchdogPolicy.ProbeEveryZeroTicks; i++)
            Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
    }

    [Fact]
    public void Late_failure_after_frames_already_arrived_is_ignored()
    {
        var p = new StreamWatchdogPolicy();
        for (var i = 0; i < StreamWatchdogPolicy.MaxProbeFailures; i++)
        {
            TickToNextProbe(p);
            p.Tick(1);
            p.OnProbeFailed();
        }
        Assert.False(p.ProbeBroken);
    }

    [Fact]
    public void Repaint_restarts_repeat_while_each_restart_brings_frames_back()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            Assert.Equal(WatchdogAction.Restart, FreezeOnce(p));
            p.Tick(1); // the recreated session's initial frame
            Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
            p.OnProbe();
            p.Tick(1); // the next repaint reaches WGC: the restart fixed the capture
        }
        Assert.False(p.RepaintRestartsExhausted);
    }

    [Fact]
    public void At_the_cap_probes_continue_but_a_frameless_repaint_waits_for_the_ceiling()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        var restarts = 0;
        for (var i = 1; i <= StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            var action = p.Tick(0);
            if (action == WatchdogAction.Probe) p.OnProbe();
            if (action == WatchdogAction.Restart)
            {
                restarts++;
                Assert.Equal(StreamWatchdogPolicy.CeilingZeroTicks, i);
            }
        }
        Assert.Equal(1, restarts);
    }

    [Fact]
    public void At_the_cap_a_probe_frame_resets_the_cap()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
        p.OnProbe();
        p.Tick(1); // the repaint reaches WGC
        Assert.False(p.RepaintRestartsExhausted);
        Assert.Equal(WatchdogAction.Restart, FreezeOnce(p));
    }

    [Fact]
    public void A_non_initial_frame_resumes_repaint_restarts()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        Assert.True(p.RepaintRestartsExhausted);
        p.Tick(1);
        Assert.False(p.RepaintRestartsExhausted);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void Ceiling_restart_initial_frame_does_not_reset_the_repaint_cap()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        var last = WatchdogAction.None;
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            last = p.Tick(0);
            if (last == WatchdogAction.Probe) p.OnProbe();
        }
        Assert.Equal(WatchdogAction.Restart, last);
        p.Tick(1);
        Assert.True(p.RepaintRestartsExhausted);
    }

    [Fact]
    public void Unanswered_probe_times_out_as_a_failure_and_probing_resumes()
    {
        var p = new StreamWatchdogPolicy();
        TickToNextProbe(p);
        for (var i = 0; i < StreamWatchdogPolicy.ProbeTimeoutTicks; i++) p.Tick(0);
        Assert.False(p.ProbeInFlight);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void Probes_that_never_answer_stop_probing_and_the_ceiling_still_recovers()
    {
        var p = new StreamWatchdogPolicy();
        var actions = new System.Collections.Generic.List<WatchdogAction>();
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks; i++) actions.Add(p.Tick(0));
        Assert.True(actions.FindAll(a => a == WatchdogAction.Probe).Count <= StreamWatchdogPolicy.MaxProbeFailures);
        Assert.Equal(WatchdogAction.Restart, actions[^1]);
    }

    [Fact]
    public void Failing_probes_stop_probing_and_the_ceiling_still_recovers()
    {
        var p = new StreamWatchdogPolicy();
        FailProbes(p, StreamWatchdogPolicy.MaxProbeFailures);
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
        FailProbes(p, StreamWatchdogPolicy.MaxProbeFailures - 1);
        TickToNextProbe(p);
        p.OnProbe();
        Assert.False(p.ProbeBroken);
    }

    [Fact]
    public void Probing_resumes_after_frames_flow_again()
    {
        var p = new StreamWatchdogPolicy();
        FailProbes(p, StreamWatchdogPolicy.MaxProbeFailures);
        Assert.True(p.ProbeBroken);
        p.Tick(1);
        Assert.False(p.ProbeBroken);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void Probing_resumes_after_a_ceiling_restart()
    {
        var p = new StreamWatchdogPolicy();
        FailProbes(p, StreamWatchdogPolicy.MaxProbeFailures);
        var action = WatchdogAction.None;
        for (var i = p.ZeroTicks; i < StreamWatchdogPolicy.CeilingZeroTicks; i++) action = p.Tick(0);
        Assert.Equal(WatchdogAction.Restart, action);
        Assert.False(p.ProbeBroken);
    }

    private static WatchdogAction FreezeOnce(StreamWatchdogPolicy p)
    {
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
        p.OnProbe();
        p.Tick(0);
        return p.Tick(0);
    }

    private static void ExhaustRepaintRestarts(StreamWatchdogPolicy p)
    {
        for (var i = 0; i < StreamWatchdogPolicy.MaxRepaintRestarts; i++)
        {
            Assert.Equal(WatchdogAction.Restart, FreezeOnce(p));
            p.Tick(1); // initial frame only
        }
        Assert.True(p.RepaintRestartsExhausted);
    }

    private static void FailProbes(StreamWatchdogPolicy p, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
            p.OnProbeFailed();
        }
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
