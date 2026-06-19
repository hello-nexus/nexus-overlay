using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>
/// Pure reconcile math for the monitor-panel kiosks: which kiosks to spawn
/// and which to close, given the service's assignments, the currently
/// attached displays, and the kiosks already up (displayId -> panelDeviceId).
/// A kiosk exists iff its display is both assigned and attached AND it hosts
/// the assignment's current panelDeviceId - demote + re-promote within one
/// poll window swaps the device id on the same display, which must close the
/// stale kiosk and spawn a fresh one. An unplugged monitor closes the kiosk
/// while the service keeps the assignment for replug.
/// </summary>
internal static class MonitorKioskPlan
{
    public readonly record struct SpawnEntry(string DisplayId, string PanelDeviceId);

    public static (List<SpawnEntry> Spawn, List<string> Close) Compute(
        IReadOnlyDictionary<string, string> assignments,
        IReadOnlyCollection<string> attachedDisplayIds,
        IReadOnlyDictionary<string, string> existingKiosks)
    {
        var attached = new HashSet<string>(attachedDisplayIds, System.StringComparer.Ordinal);

        var spawn = new List<SpawnEntry>();
        var keep = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var (displayId, panelDeviceId) in assignments)
        {
            if (!attached.Contains(displayId)) continue;
            if (existingKiosks.TryGetValue(displayId, out var existingDeviceId)
                && string.Equals(existingDeviceId, panelDeviceId, System.StringComparison.Ordinal))
            {
                keep.Add(displayId);
                continue;
            }
            spawn.Add(new SpawnEntry(displayId, panelDeviceId));
        }

        var close = new List<string>();
        foreach (var existing in existingKiosks.Keys)
        {
            if (!keep.Contains(existing))
                close.Add(existing);
        }
        return (spawn, close);
    }
}
