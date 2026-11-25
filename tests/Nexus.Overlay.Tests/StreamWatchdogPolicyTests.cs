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
    public void At_the_cap_probes_continue_and_a_frameless_repaint_rebuilds()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
        p.OnProbe();
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Rebuild, p.Tick(0));
        Assert.Equal(0, p.ZeroTicks);
        Assert.False(p.ProbeInFlight);
        Assert.False(p.RepaintRestartsExhausted);
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
    public void Ceiling_restarts_that_only_yield_initial_frames_escalate_to_a_rebuild()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < StreamWatchdogPolicy.MaxStuckRestarts; cycle++)
        {
            Assert.Equal(WatchdogAction.Restart, SilentToCeiling(p));
            p.Tick(1); // initial frame only
        }
        Assert.Equal(WatchdogAction.Rebuild, SilentToCeiling(p));
    }

    [Fact]
    public void A_non_initial_frame_clears_the_stuck_count()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < StreamWatchdogPolicy.MaxStuckRestarts; cycle++)
        {
            Assert.Equal(WatchdogAction.Restart, SilentToCeiling(p));
            p.Tick(1);
        }
        p.Tick(1); // frames flow again
        Assert.Equal(WatchdogAction.Restart, SilentToCeiling(p));
    }

    [Fact]
    public void Rebuild_resets_the_policy_for_a_fresh_host()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
        p.OnProbe();
        p.Tick(0);
        Assert.Equal(WatchdogAction.Rebuild, p.Tick(0));
        Assert.Equal(WatchdogAction.Restart, FreezeOnce(p));
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

    [Fact]
    public void A_frameless_repaint_at_the_cap_never_rebuilds_while_locked()
    {
        var p = new StreamWatchdogPolicy();
        ExhaustRepaintRestarts(p);
        for (var i = 0; i < 200; i++)
        {
            var action = p.Tick(0, sessionLocked: true);
            if (action == WatchdogAction.Probe) p.OnProbe();
            Assert.NotEqual(WatchdogAction.Rebuild, action);
        }
    }

    [Fact]
    public void Ceiling_restarts_at_the_stuck_cap_hold_while_locked_and_rebuild_after_unlock()
    {
        var p = new StreamWatchdogPolicy();
        for (var cycle = 0; cycle < StreamWatchdogPolicy.MaxStuckRestarts; cycle++)
        {
            Assert.Equal(WatchdogAction.Restart, SilentToCeiling(p));
            p.Tick(1);
        }
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            var action = p.Tick(0, sessionLocked: true);
            if (action == WatchdogAction.Probe) p.OnProbeFailed();
            Assert.NotEqual(WatchdogAction.Rebuild, action);
        }
        Assert.Equal(WatchdogAction.Rebuild, SilentToCeiling(p));
    }

    private static WatchdogAction SilentToCeiling(StreamWatchdogPolicy p)
    {
        var last = WatchdogAction.None;
        for (var i = 0; i < StreamWatchdogPolicy.CeilingZeroTicks; i++)
        {
            last = p.Tick(0);
            if (last == WatchdogAction.Probe) p.OnProbeFailed();
        }
        return last;
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

    // One answered repaint with no WGC frame, graded after the fast grace ticks; returns the verdict action.
    private static WatchdogAction AnsweredMiss(StreamWatchdogPolicy p)
    {
        p.OnProbe();
        for (var i = 0; i < StreamWatchdogPolicy.FastDetectGraceTicks; i++) Assert.Equal(WatchdogAction.None, p.Tick(0));
        return p.Tick(0);
    }

    [Fact]
    public void A_fast_detect_host_waits_for_its_initial_frame_without_probing()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        Assert.True(p.FastDetectWindow);
        for (var i = 0; i < 20; i++) Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.False(p.ProbeInFlight);
    }

    [Fact]
    public void Two_answered_repaints_without_a_frame_escalate_to_software()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        Assert.Equal(WatchdogAction.None, p.Tick(1));
        Assert.True(p.FastDetectWindow);
        Assert.Equal(WatchdogAction.Probe, p.Tick(0)); // first silent tick probes at once
        Assert.Equal(WatchdogAction.Probe, AnsweredMiss(p)); // first miss asks again
        Assert.Equal(WatchdogAction.RebuildSoftware, AnsweredMiss(p));
        Assert.False(p.ProbeInFlight);
    }

    [Fact]
    public void A_single_miss_then_a_frame_stays_on_gpu_and_the_window_closes()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        p.Tick(1);
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
        Assert.Equal(WatchdogAction.Probe, AnsweredMiss(p));
        p.OnProbe();
        Assert.Equal(WatchdogAction.None, p.Tick(1));
        Assert.False(p.FastDetectWindow);
        for (var i = 0; i < 200; i++) Assert.NotEqual(WatchdogAction.RebuildSoftware, p.Tick(i % 7 == 0 ? 1 : 0));
    }

    [Fact]
    public void Fast_detect_off_keeps_the_old_ladder_for_the_same_sequence()
    {
        var p = new StreamWatchdogPolicy();
        p.Tick(1);
        Assert.False(p.FastDetectWindow);
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
        p.OnProbe();
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Restart, p.Tick(0));
    }

    [Fact]
    public void Probe_timeouts_in_the_fast_window_never_latch_probe_broken_and_the_ladder_starts_armed()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        p.Tick(1);
        for (var timeout = 0; timeout < StreamWatchdogPolicy.MaxProbeFailures + 2; timeout++)
        {
            Assert.Equal(WatchdogAction.Probe, p.Tick(0));
            for (var i = 0; i < StreamWatchdogPolicy.ProbeTimeoutTicks - 1; i++) Assert.Equal(WatchdogAction.None, p.Tick(0));
            Assert.Equal(WatchdogAction.None, p.Tick(0)); // timeout tick
            Assert.False(p.ProbeBroken);
        }
        // Drive the window to its bound: the ladder is then fully armed.
        while (p.FastDetectWindow) p.Tick(0);
        Assert.False(p.ProbeBroken);
        Assert.False(p.ProbeInFlight);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
    }

    [Fact]
    public void A_probe_timeout_outside_the_window_still_counts()
    {
        var p = new StreamWatchdogPolicy();
        p.Tick(30);
        for (var i = 0; i < StreamWatchdogPolicy.MaxProbeFailures; i++)
        {
            Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
            p.OnProbeFailed();
        }
        Assert.True(p.ProbeBroken);
    }

    [Fact]
    public void A_restart_never_reopens_the_fast_window()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        p.Tick(1);
        while (p.FastDetectWindow) p.Tick(0);
        Assert.Equal(WatchdogAction.Probe, TickToNextProbe(p));
        p.OnProbe();
        p.Tick(0);
        Assert.Equal(WatchdogAction.Restart, p.Tick(0));
        Assert.False(p.FastDetectWindow);
        p.Tick(1); // restart's initial frame
        Assert.False(p.FastDetectWindow);
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Probe, p.Tick(0)); // old cadence: every second zero tick
        Assert.False(p.FastDetectWindow);
    }

    [Fact]
    public void A_host_that_framed_then_stalled_uses_the_old_ladder()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        p.Tick(30);
        Assert.False(p.FastDetectWindow);
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Probe, p.Tick(0));
        p.OnProbe();
        Assert.Equal(WatchdogAction.None, p.Tick(0));
        Assert.Equal(WatchdogAction.Restart, p.Tick(0));
        p.Tick(1); // the new session's initial frame
        for (var i = 1; i < StreamWatchdogPolicy.MaxRepaintRestarts; i++)
        {
            Assert.Equal(WatchdogAction.Restart, FreezeOnce(p));
            p.Tick(1);
        }
        Assert.Equal(WatchdogAction.Rebuild, FreezeOnce(p));
    }

    [Fact]
    public void A_locked_session_neither_probes_in_the_window_nor_uses_the_fast_tick()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        p.Tick(1, sessionLocked: true);
        Assert.False(p.FastDetectWindow);
        // Old cadence while locked: nothing on the first silent tick, a probe on the second.
        Assert.Equal(WatchdogAction.None, p.Tick(0, sessionLocked: true));
        Assert.Equal(WatchdogAction.Probe, p.Tick(0, sessionLocked: true));
        p.OnProbe();
        p.Tick(0, sessionLocked: true);
        for (var i = 0; i < 30; i++) Assert.NotEqual(WatchdogAction.RebuildSoftware, p.Tick(0, sessionLocked: true));
    }

    [Fact]
    public void Locked_ticks_do_not_consume_the_window_and_it_resumes_on_unlock()
    {
        var p = new StreamWatchdogPolicy(fastDetect: true);
        p.Tick(1, sessionLocked: true);
        for (var i = 0; i < StreamWatchdogPolicy.FastDetectMaxTicks * 2; i++) p.Tick(0, sessionLocked: true);
        Assert.False(p.FastDetectWindow);
        Assert.False(p.ProbeBroken);
        p.Tick(0);
        Assert.True(p.FastDetectWindow);
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
