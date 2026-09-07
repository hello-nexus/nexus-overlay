using System;
using System.Collections.Generic;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Hosts one fullscreen <see cref="PanelKioskWindow"/> per monitor-panel
/// assignment (user-promoted monitors, distinct from the auto-detected Y70
/// kiosk). Reconciled against the overlay state on every poll/push and on
/// WM_DISPLAYCHANGE; the plan math lives in <see cref="MonitorKioskPlan"/>.
/// All calls run on the message-loop thread.
/// </summary>
internal sealed class MonitorKioskManager
{
    private sealed class KioskEntry
    {
        public required PanelKioskWindow Window { get; init; }
        public required string PanelDeviceId { get; init; }
        public required bool Reserve { get; set; }
        public required bool SeeThrough { get; init; }
    }

    private readonly string _serviceOrigin;
    private readonly Dictionary<string, KioskEntry> _kiosks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unconfirmedRecreates = new(StringComparer.Ordinal);

    public MonitorKioskManager(string serviceOrigin)
    {
        _serviceOrigin = serviceOrigin;
    }

    public int Count => _kiosks.Count;

    public void Reconcile(IReadOnlyList<DisplayAssignment> assignments, string pairedToken)
    {
        List<string>? unhealthy = null;
        foreach (var (displayId, entry) in _kiosks)
        {
            var attempts = _unconfirmedRecreates.TryGetValue(displayId, out var n) ? n : 0;
            if (entry.Window.HasConfirmedContent && !entry.Window.Failed)
            {
                _unconfirmedRecreates.Remove(displayId);
                continue;
            }
            if (entry.Window.Unhealthy(attempts))
            {
                _unconfirmedRecreates[displayId] = attempts + 1;
                Log.Warn($"monitor-kiosk unhealthy display={displayId} failed={entry.Window.Failed} age={entry.Window.AgeMs} ms (attempt {attempts + 1}); recreating");
                (unhealthy ??= new List<string>()).Add(displayId);
            }
        }
        if (unhealthy is not null)
        {
            foreach (var displayId in unhealthy) CloseKiosk(displayId, "unhealthy", keepWatchdogState: true);
        }

        var monitors = Monitors.Enumerate();
        var byDisplayId = new Dictionary<string, MonitorInfo>(StringComparer.Ordinal);
        foreach (var monitor in monitors)
        {
            var id = DisplayIdentity.ResolveStableId(monitor.DeviceName);
            if (!string.IsNullOrEmpty(id))
                byDisplayId.TryAdd(id, monitor);
        }

        var assignmentMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var reserveById = new Dictionary<string, bool>(StringComparer.Ordinal);
        var seeThroughById = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrEmpty(assignment.DisplayId) || string.IsNullOrEmpty(assignment.PanelDeviceId)) continue;
            assignmentMap[assignment.DisplayId] = assignment.PanelDeviceId;
            reserveById[assignment.DisplayId] = assignment.ReserveMonitor;
            seeThroughById[assignment.DisplayId] = assignment.Backdrop == "desktop";
        }

        // Arrangement/resolution changes don't alter the attached-id set, so
        // the plan alone would no-op while the kiosk window no longer covers
        // its monitor (and the guard holds a stale HMONITOR). Refit in place
        // and keep the kiosk: a close+respawn shows the desktop from the
        // destroy until the replacement WebView2's first paint, which the user
        // sees on a display that rotates live (Xeneon Edge). The kiosk's own
        // WM_DISPLAYCHANGE usually refits before this runs, leaving the compare
        // below equal.
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        List<string>? backdropChanged = null;
        foreach (var (displayId, entry) in _kiosks)
        {
            if (byDisplayId.TryGetValue(displayId, out var monitor)
                && !SameBounds(entry.Window.MonitorBounds, monitor.Bounds))
            {
                try
                {
                    entry.Window.Refit(monitor);
                    Log.Info($"monitor-kiosk refit display={displayId} -> {monitor.Bounds.Width}x{monitor.Bounds.Height}");
                }
                catch (Exception ex) { Log.Error($"monitor-kiosk refit {displayId}: {ex.Message}"); }
            }
            // The background brush and the WebView2 default background are
            // fixed at creation, so a backdrop switch is a recreate. The plan
            // only closes kiosks whose display lost its assignment, so this
            // one has to be closed here; omitting it from `existing` is what
            // makes the plan spawn the replacement in the same pass.
            // Only when the display is still assigned: an unassigned one has
            // no desired backdrop, and the plan's close path owns it with the
            // accurate reason.
            var wantSeeThrough = seeThroughById.TryGetValue(displayId, out var st) && st;
            if (assignmentMap.ContainsKey(displayId) && wantSeeThrough != entry.SeeThrough)
            {
                (backdropChanged ??= new List<string>()).Add(displayId);
                continue;
            }
            entry.Window.ReassertTaskbar();
            existing[displayId] = entry.PanelDeviceId;
        }
        if (backdropChanged is not null)
        {
            foreach (var displayId in backdropChanged)
            {
                CloseKiosk(displayId, "backdrop changed");
            }
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
                var reserve = reserveById.TryGetValue(entry.DisplayId, out var r) ? r : true;
                var seeThrough = seeThroughById.TryGetValue(entry.DisplayId, out var st2) && st2;
                var url = $"{_serviceOrigin}/panel/{Uri.EscapeDataString(entry.PanelDeviceId)}?token={Uri.EscapeDataString(pairedToken)}{(seeThrough ? "&backdrop=desktop" : "")}";
                _kiosks[entry.DisplayId] = new KioskEntry
                {
                    Window = new PanelKioskWindow(monitor, url, reserve, refitOnDisplayChange: true, seeThrough: seeThrough),
                    PanelDeviceId = entry.PanelDeviceId,
                    Reserve = reserve,
                    SeeThrough = seeThrough,
                };
                Log.Info($"monitor-kiosk opened display={entry.DisplayId} device={entry.PanelDeviceId} monitor={monitor.Index} guard={reserve} seeThrough={seeThrough}");
            }
            catch (Exception ex)
            {
                Log.Error($"monitor-kiosk spawn {entry.DisplayId}: {ex.Message}");
            }
        }

        // Per-panel reserve toggled on a live kiosk: flip the guard in place.
        foreach (var (displayId, entry) in _kiosks)
        {
            if (!reserveById.TryGetValue(displayId, out var reserve) || reserve == entry.Reserve) continue;
            entry.Reserve = reserve;
            try { entry.Window.SetMonitorGuard(reserve); } catch { /* best-effort */ }
            Log.Info($"monitor-kiosk guard display={displayId} -> {reserve}");
        }
    }

    private void CloseKiosk(string displayId, string reason, bool keepWatchdogState = false)
    {
        // A departing/demoted display starts fresh on re-promote; only a
        // watchdog recreate carries its attempt count into the respawn.
        if (!keepWatchdogState) _unconfirmedRecreates.Remove(displayId);
        if (!_kiosks.Remove(displayId, out var entry)) return;
        try { entry.Window.Dispose(); }
        catch (Exception ex) { Log.Error($"monitor-kiosk dispose {displayId}: {ex.Message}"); }
        Log.Info($"monitor-kiosk closed display={displayId} ({reason})");
    }

    private static bool SameBounds(Native.RECT a, Native.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void CloseAll()
    {
        foreach (var entry in _kiosks.Values)
        {
            try { entry.Window.Dispose(); } catch { /* best-effort */ }
        }
        _kiosks.Clear();
        _unconfirmedRecreates.Clear();
    }
}
