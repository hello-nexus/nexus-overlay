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
    // EDID names returned by EnumDisplayDevicesW are usually just "Generic PnP
    // Monitor" - Windows doesn't surface the real friendly name through that
    // API. HYTE panels are identified instead by the hardware DeviceID (read
    // through DisplayIdentity so the ACTIVE-child rule applies here too), which
    // embeds the panel controller name and is stable per model. HYTE ships the
    // Y70 with several controllers (Realtek/BOE variants), so match any. Keep
    // in sync with Y70DisplayProtocol.DdcPanelHardwareNames in nexus-service -
    // a separate assembly, so the constant can't be shared.
    private static readonly string[] KnownPanelHardwareNames =
    {
        "RTK2234", "RTK0004", "RTD1100", "RTK1234", "BOE2143", "RTK409A", "RTK2345",
    };

    // Lower is better; -1 is no match. The GT screen (first entry) is never used
    // alongside the Y70 touch panel, so it wins when both are connected.
    internal static int PanelRank(string deviceId)
    {
        for (var i = 0; i < KnownPanelHardwareNames.Length; i++)
        {
            if (deviceId.IndexOf(KnownPanelHardwareNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    // Every name above is a Y70 variant, so a Find() match is always this surface.
    internal const string Surface = "y70";

    // Last scan result. The kiosk poll runs Find() every 5 s; logging only when
    // the monitor set or match outcome changes keeps the steady state silent
    // instead of spamming the log six lines per tick.
    private static string _lastScanSignature = string.Empty;

    /// <summary>
    /// Returns the <see cref="MonitorInfo"/> of the first connected display
    /// whose hardware DeviceID contains a known HYTE panel controller name,
    /// preferring the earliest entry of <see cref="KnownPanelHardwareNames"/>;
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

            var rank = PanelRank(deviceId);
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
