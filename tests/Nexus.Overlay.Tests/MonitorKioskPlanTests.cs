using System.Collections.Generic;
using Xunit;

namespace Nexus.Overlay.Tests;

public class MonitorKioskPlanTests
{
    private static Dictionary<string, string> Map(params (string k, string v)[] entries)
    {
        var map = new Dictionary<string, string>();
        foreach (var (k, v) in entries) map[k] = v;
        return map;
    }

    [Fact]
    public void Spawns_assigned_attached_displays_without_kiosks()
    {
        var (spawn, close) = MonitorKioskPlan.Compute(
            Map(("disp-a", "dev-1"), ("disp-b", "dev-2")),
            attachedDisplayIds: new[] { "disp-a", "disp-b", "disp-c" },
            existingKiosks: Map(("disp-a", "dev-1")));

        Assert.Single(spawn);
        Assert.Equal(("disp-b", "dev-2"), (spawn[0].DisplayId, spawn[0].PanelDeviceId));
        Assert.Empty(close);
    }

    [Fact]
    public void Closes_kiosk_when_assignment_removed()
    {
        var (spawn, close) = MonitorKioskPlan.Compute(
            Map(),
            attachedDisplayIds: new[] { "disp-a" },
            existingKiosks: Map(("disp-a", "dev-1")));

        Assert.Empty(spawn);
        Assert.Equal(new[] { "disp-a" }, close);
    }

    [Fact]
    public void Closes_kiosk_when_monitor_unplugged_but_keeps_assignment_inert()
    {
        var (spawn, close) = MonitorKioskPlan.Compute(
            Map(("disp-a", "dev-1")),
            attachedDisplayIds: new string[0],
            existingKiosks: Map(("disp-a", "dev-1")));

        Assert.Empty(spawn);
        Assert.Equal(new[] { "disp-a" }, close);
    }

    [Fact]
    public void Replug_respawns_the_assigned_display()
    {
        var (spawn, close) = MonitorKioskPlan.Compute(
            Map(("disp-a", "dev-1")),
            attachedDisplayIds: new[] { "disp-a" },
            existingKiosks: Map());

        Assert.Single(spawn);
        Assert.Empty(close);
    }

    [Fact]
    public void Device_id_swap_on_same_display_closes_and_respawns()
    {
        // Demote + re-promote within one poll window: same display, new record.
        var (spawn, close) = MonitorKioskPlan.Compute(
            Map(("disp-a", "dev-2")),
            attachedDisplayIds: new[] { "disp-a" },
            existingKiosks: Map(("disp-a", "dev-1")));

        Assert.Equal(new[] { "disp-a" }, close);
        Assert.Single(spawn);
        Assert.Equal("dev-2", spawn[0].PanelDeviceId);
    }

    [Fact]
    public void Steady_state_is_a_no_op()
    {
        var (spawn, close) = MonitorKioskPlan.Compute(
            Map(("disp-a", "dev-1"), ("disp-b", "dev-2")),
            attachedDisplayIds: new[] { "disp-a", "disp-b" },
            existingKiosks: Map(("disp-a", "dev-1"), ("disp-b", "dev-2")));

        Assert.Empty(spawn);
        Assert.Empty(close);
    }
}
