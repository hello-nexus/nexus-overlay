using System;
using System.Text;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Matches the connected displays against known HYTE panel models so the
/// overlay can decide which monitor to host the panel kiosk window on.
/// </summary>
internal static class PanelDisplay
{
    // Every PanelRank name is a Y70 variant, so a Find() match is always this surface.
    internal const string Surface = "y70";

    // Last scan result. The kiosk poll runs Find() every 5 s; logging only when
    // the monitor set or match outcome changes keeps the steady state silent
    // instead of spamming the log six lines per tick.
    private static string _lastScanSignature = string.Empty;

    /// <summary>
    /// Returns the <see cref="MonitorInfo"/> of the first connected display
    /// whose hardware DeviceID contains a known HYTE panel controller name,
    /// preferring the earliest entry of <see cref="PanelRank.KnownPanelHardwareNames"/>;
    /// null if none match.
    /// </summary>
    public static MonitorInfo? Find()
    {
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return null;

        MonitorInfo? match = null;
        var matchRank = -1;
        var sig = new StringBuilder();
        foreach (var m in monitors)
        {
            if (string.IsNullOrEmpty(m.DeviceName)) continue;

            var deviceId = DisplayIdentity.ReadDrivenMonitorDeviceId(m.DeviceName);
            sig.Append(m.Index).Append('=').Append(deviceId.Length == 0 ? "?" : deviceId).Append(' ');

            var rank = PanelRank.Of(deviceId);
            if (rank >= 0 && (match is null || rank < matchRank))
            {
                match = m;
                matchRank = rank;
            }
        }

        var outcome = match is null ? "no HYTE panel" : $"matched index={match.Index}";
        var signature = $"{sig}=> {outcome}";
        if (signature != _lastScanSignature)
        {
            _lastScanSignature = signature;
            Log.Info($"panel display scan ({monitors.Count}): {sig.ToString().TrimEnd()} => {outcome}");
        }
        return match;
    }

}
