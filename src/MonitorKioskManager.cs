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
        public required KioskTraits Traits { get; set; }
    }

    private readonly string _serviceOrigin;
    private readonly Dictionary<string, KioskEntry> _kiosks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _unconfirmedRecreates = new(StringComparer.Ordinal);

    public MonitorKioskManager(string serviceOrigin)
    {
        _serviceOrigin = serviceOrigin;
    }

    public int Count => _kiosks.Count;

    /// <summary>Raised when a live kiosk's monitor became primary and its guards were dropped in place.</summary>
    public Action? PrimaryPromoted { get; set; }

    /// <summary>Live kiosk windows currently outside the topmost band.</summary>
    public IEnumerable<PanelKioskWindow> NonTopmostWindows()
    {
        foreach (var entry in _kiosks.Values)
        {
            if (entry.Window.Hwnd != IntPtr.Zero && !entry.Window.Topmost) yield return entry.Window;
        }
    }

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
        var traitsById = new Dictionary<string, KioskTraits>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrEmpty(assignment.DisplayId) || string.IsNullOrEmpty(assignment.PanelDeviceId)) continue;
            assignmentMap[assignment.DisplayId] = assignment.PanelDeviceId;
            // An unplugged display has no monitor to ask; it is not spawned anyway.
            var onPrimary = byDisplayId.TryGetValue(assignment.DisplayId, out var m) && m.Primary;
            traitsById[assignment.DisplayId] = KioskPolicy.Resolve(assignment, onPrimary);
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
        List<string>? traitsChanged = null;
        foreach (var (displayId, entry) in _kiosks)
        {
            if (byDisplayId.TryGetValue(displayId, out var monitor)
                && !SameBounds(entry.Window.PlacedBounds, entry.Window.TargetRect(monitor)))
            {
                try
                {
                    entry.Window.Refit(monitor);
                    Log.Info($"monitor-kiosk refit display={displayId} -> {entry.Window.PlacedBounds.Width}x{entry.Window.PlacedBounds.Height}");
                }
                catch (Exception ex) { Log.Error($"monitor-kiosk refit {displayId}: {ex.Message}"); }
            }
            // The background brush and the WebView2 default background are
            // fixed at creation, and the primary display wires differently
            // (no guards, no touch-cursor guard), so a backdrop or primary
            // switch is a recreate. The plan only closes kiosks whose display
            // lost its assignment, so this one has to be closed here; omitting
            // it from `existing` is what makes the plan spawn the replacement
            // in the same pass.
            // Only when the display is still assigned: an unassigned one has
            // no desired traits, and the plan's close path owns it with the
            // accurate reason.
            if (traitsById.TryGetValue(displayId, out var wanted) && KioskPolicy.NeedsRespawn(entry.Traits, wanted))
            {
                (traitsChanged ??= new List<string>()).Add(displayId);
                continue;
            }
            entry.Window.ReassertTaskbar();
            existing[displayId] = entry.PanelDeviceId;
        }
        if (traitsChanged is not null)
        {
            foreach (var displayId in traitsChanged)
            {
                CloseKiosk(displayId, "backdrop or primary changed");
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
                if (!traitsById.TryGetValue(entry.DisplayId, out var traits)) continue;
                var seeThrough = traits.SeeThrough;
                var url = $"{_serviceOrigin}/panel/{Uri.EscapeDataString(entry.PanelDeviceId)}?token={Uri.EscapeDataString(pairedToken)}{(seeThrough ? "&backdrop=desktop" : "")}";
                _kiosks[entry.DisplayId] = new KioskEntry
                {
                    Window = new PanelKioskWindow(monitor, url, traits.Reserve, refitOnDisplayChange: true, seeThrough: seeThrough,
                        topmost: traits.Topmost, clickThrough: traits.ClickThrough, isPrimary: traits.IsPrimary),
                    PanelDeviceId = entry.PanelDeviceId,
                    Traits = traits,
                };
                var spawned = _kiosks[entry.DisplayId];
                spawned.Window.PrimaryPromoted = () =>
                {
                    spawned.Traits = spawned.Traits with { IsPrimary = true, Reserve = false, KeepCursorOff = false };
                    PrimaryPromoted?.Invoke();
                };
                Log.Info($"monitor-kiosk opened display={entry.DisplayId} device={entry.PanelDeviceId} monitor={monitor.Index} guard={traits.Reserve} seeThrough={seeThrough} topmost={traits.Topmost} clickThrough={traits.ClickThrough} primary={traits.IsPrimary}");
            }
            catch (Exception ex)
            {
                Log.Error($"monitor-kiosk spawn {entry.DisplayId}: {ex.Message}");
            }
        }

        // Apply the live-changeable traits in place.
        foreach (var (displayId, entry) in _kiosks)
        {
            if (!traitsById.TryGetValue(displayId, out var wanted)) continue;
            var live = entry.Traits;
            if (wanted.Reserve != live.Reserve)
            {
                try { entry.Window.SetMonitorGuard(wanted.Reserve); } catch { /* best-effort */ }
                Log.Info($"monitor-kiosk guard display={displayId} -> {wanted.Reserve}");
            }
            if (wanted.ClickThrough != live.ClickThrough)
            {
                try { entry.Window.SetClickThrough(wanted.ClickThrough); } catch { /* best-effort */ }
                Log.Info($"monitor-kiosk clickThrough display={displayId} -> {wanted.ClickThrough}");
            }
            // Called on every pass: windows moved onto the panel stack above a
            // non-topmost kiosk, so it must re-sink on each call.
            try { entry.Window.SetTopmost(wanted.Topmost); } catch { /* best-effort */ }
            if (wanted.Topmost != live.Topmost) Log.Info($"monitor-kiosk topmost display={displayId} -> {wanted.Topmost}");
            entry.Traits = wanted;
        }
    }

    /// <summary>Adds the monitor rect of every kiosk whose panel keeps the mouse off.</summary>
    public void AddKeepOffBounds(List<Native.RECT> bounds)
    {
        foreach (var entry in _kiosks.Values)
        {
            if (entry.Traits.KeepCursorOff && !entry.Window.IsPrimary && entry.Window.Hwnd != IntPtr.Zero) bounds.Add(entry.Window.MonitorBounds);
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
