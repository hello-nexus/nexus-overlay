using System.Collections.Generic;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Pure reconcile math for the off-screen stream hosts: which capture
/// sessions to spawn and which to close, given the sessions the service
/// wants and the hosts already up. Both lists are sorted ordinal so the
/// reconcile order is deterministic.
/// </summary>
internal static class StreamHostPlan
{
    public static (IReadOnlyList<string> Spawn, IReadOnlyList<string> Close) Compute(
        IReadOnlyCollection<string> desiredSessionIds,
        IReadOnlyCollection<string> existingSessionIds)
    {
        var desired = new HashSet<string>(desiredSessionIds, System.StringComparer.Ordinal);
        var existing = new HashSet<string>(existingSessionIds, System.StringComparer.Ordinal);

        var spawn = new List<string>();
        foreach (var id in desired)
        {
            if (!existing.Contains(id)) spawn.Add(id);
        }

        var close = new List<string>();
        foreach (var id in existing)
        {
            if (!desired.Contains(id)) close.Add(id);
        }

        spawn.Sort(System.StringComparer.Ordinal);
        close.Sort(System.StringComparer.Ordinal);
        return (spawn, close);
    }
}
