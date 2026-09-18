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
        "RTK0004", "RTD1100", "RTK1234", "RTK2234", "BOE2143", "RTK409A", "RTK2345",
    };

    // Last scan result. The kiosk poll runs Find() every 5 s; logging only when
    // the monitor set or match outcome changes keeps the steady state silent
    // instead of spamming the log six lines per tick.
    private static string _lastScanSignature = string.Empty;

    /// <summary>
    /// Returns the <see cref="MonitorInfo"/> of the first connected display
    /// whose hardware DeviceID contains a known HYTE panel controller name;
    /// null if none match.
    /// </summary>
    public static MonitorInfo? Find()
    {
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return null;

        MonitorInfo? match = null;
        var sig = new StringBuilder();
        foreach (var m in monitors)
        {
            if (string.IsNullOrEmpty(m.DeviceName)) continue;

            var deviceId = DisplayIdentity.ReadDrivenMonitorDeviceId(m.DeviceName);
            sig.Append(m.Index).Append('=').Append(deviceId.Length == 0 ? "?" : deviceId).Append(' ');

            if (match is null)
            {
                foreach (var name in KnownPanelHardwareNames)
                {
                    if (deviceId.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = m;
                        break;
                    }
                }
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
