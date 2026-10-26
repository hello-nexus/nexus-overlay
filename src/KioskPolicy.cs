using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>What a monitor kiosk must do, after the primary-display rules.</summary>
internal readonly record struct KioskTraits(
    bool IsPrimary, bool Reserve, bool KeepCursorOff, bool SeeThrough, bool ClickThrough, bool Topmost)
{
    /// <summary>Taskbar hiding never applies on the primary display.</summary>
    public bool AllowTaskbarGuard => !IsPrimary;
}

internal static class KioskPolicy
{
    /// <summary>
    /// Fold the service assignment and the host's own view of the monitor.
    /// The primary display never runs the foreign-window guard, the keep-mouse-off
    /// wall or the taskbar guard, whatever the stored flags say: a stale or
    /// older-build record must not strand the user's main desktop. Click-through
    /// only exists on a see-through panel.
    /// </summary>
    public static KioskTraits Resolve(DisplayAssignment a, bool monitorIsPrimary)
    {
        var primary = a.IsPrimary || monitorIsPrimary;
        var seeThrough = string.Equals(a.Backdrop, "desktop", StringComparison.Ordinal);
        return new KioskTraits(
            IsPrimary: primary,
            Reserve: !primary && a.ReserveMonitor,
            KeepCursorOff: !primary && a.KeepCursorOff,
            SeeThrough: seeThrough,
            ClickThrough: seeThrough && a.ClickThrough,
            Topmost: a.AlwaysOnTop);
    }

    /// <summary>
    /// Traits baked into the window at creation (class background, WebView2
    /// default background, primary-only wiring) need a recreate; the rest is
    /// applied in place.
    /// </summary>
    public static bool NeedsRespawn(KioskTraits live, KioskTraits wanted)
        => live.SeeThrough != wanted.SeeThrough || live.IsPrimary != wanted.IsPrimary;
}

/// <summary>
/// The EnumDisplayMonitors index to stable display id map the service needs to
/// resolve the legacy overlay's monitor index. Pure so it is testable off Windows.
/// </summary>
internal static class MonitorMap
{
    public static List<OverlayMonitorEntry> Build(
        IEnumerable<(int Index, string DeviceName, bool Primary)> monitors,
        Func<string, string> resolveStableId)
    {
        var list = new List<OverlayMonitorEntry>();
        foreach (var (index, deviceName, primary) in monitors)
        {
            var id = resolveStableId(deviceName);
            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new OverlayMonitorEntry { Index = index, DisplayId = id, Primary = primary });
        }
        return list;
    }

    public static string Signature(IReadOnlyList<OverlayMonitorEntry> entries)
    {
        var parts = new string[entries.Count];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = $"{entries[i].Index}|{entries[i].DisplayId}|{(entries[i].Primary ? 1 : 0)}";
        return string.Join(";", parts);
    }
}
