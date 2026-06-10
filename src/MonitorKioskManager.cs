using System;
using System.Collections.Generic;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Hosts one fullscreen <see cref="PanelKioskWindow"/> per monitor-panel
/// assignment (user-promoted monitors, distinct from the auto-detected Y70
/// kiosk). Reconciled against GET /displays/assignments on every prefs
/// poll/push and on WM_DISPLAYCHANGE; the plan math lives in
/// <see cref="MonitorKioskPlan"/>. All calls run on the message-loop thread.
/// </summary>
internal sealed class MonitorKioskManager
{
    private sealed record KioskEntry(PanelKioskWindow Window, string PanelDeviceId);

    private readonly string _serviceOrigin;
    private readonly Dictionary<string, KioskEntry> _kiosks = new(StringComparer.Ordinal);

    public MonitorKioskManager(string serviceOrigin)
    {
        _serviceOrigin = serviceOrigin;
    }

    public int Count => _kiosks.Count;

    public void Reconcile(IReadOnlyList<DisplayAssignment> assignments, string pairedToken, bool guardMonitor)
    {
        var monitors = Monitors.Enumerate();
        var byDisplayId = new Dictionary<string, MonitorInfo>(StringComparer.Ordinal);
        foreach (var monitor in monitors)
        {
            var id = DisplayIdentity.ResolveStableId(monitor.DeviceName);
            if (!string.IsNullOrEmpty(id))
                byDisplayId.TryAdd(id, monitor);
        }

        var assignmentMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (!string.IsNullOrEmpty(assignment.DisplayId) && !string.IsNullOrEmpty(assignment.PanelDeviceId))
                assignmentMap[assignment.DisplayId] = assignment.PanelDeviceId;
        }

        // Arrangement/resolution changes don't alter the attached-id set, so
        // the plan alone would no-op while the kiosk window no longer covers
        // its monitor (and the guard holds a stale HMONITOR). Close those
        // here; the plan then respawns them at the fresh bounds.
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        var boundsChanged = new List<string>();
        foreach (var (displayId, entry) in _kiosks)
        {
            if (byDisplayId.TryGetValue(displayId, out var monitor)
                && !SameBounds(entry.Window.MonitorBounds, monitor.Bounds))
            {
                boundsChanged.Add(displayId);
                continue;
            }
            existing[displayId] = entry.PanelDeviceId;
        }
        foreach (var displayId in boundsChanged)
        {
            CloseKiosk(displayId, "bounds changed");
        }

        var (spawn, close) = MonitorKioskPlan.Compute(assignmentMap, byDisplayId.Keys, existing);

        foreach (var displayId in close)
        {
            CloseKiosk(displayId, "unassigned or unplugged");
        }

        foreach (var entry in spawn)
        {
            if (!byDisplayId.TryGetValue(entry.DisplayId, out var monitor)) continue;
            try
            {
                var url = $"{_serviceOrigin}/panel/{Uri.EscapeDataString(entry.PanelDeviceId)}?token={Uri.EscapeDataString(pairedToken)}";
                _kiosks[entry.DisplayId] = new KioskEntry(new PanelKioskWindow(monitor, url, guardMonitor), entry.PanelDeviceId);
                Log.Info($"monitor-kiosk opened display={entry.DisplayId} device={entry.PanelDeviceId} monitor={monitor.Index}");
            }
            catch (Exception ex)
            {
                Log.Error($"monitor-kiosk spawn {entry.DisplayId}: {ex.Message}");
            }
        }
    }

    private void CloseKiosk(string displayId, string reason)
    {
        if (!_kiosks.Remove(displayId, out var entry)) return;
        try { entry.Window.Dispose(); }
        catch (Exception ex) { Log.Error($"monitor-kiosk dispose {displayId}: {ex.Message}"); }
        Log.Info($"monitor-kiosk closed display={displayId} ({reason})");
    }

    private static bool SameBounds(Native.RECT a, Native.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void SetMonitorGuardAll(bool enabled)
    {
        foreach (var entry in _kiosks.Values)
        {
            try { entry.Window.SetMonitorGuard(enabled); } catch { /* best-effort */ }
        }
    }

    public void CloseAll()
    {
        foreach (var entry in _kiosks.Values)
        {
            try { entry.Window.Dispose(); } catch { /* best-effort */ }
        }
        _kiosks.Clear();
    }
}
