using System;
using System.Runtime.InteropServices;
using Qos.Overlay.Win32;

namespace Qos.Overlay;

/// <summary>
/// Matches the connected displays against known HYTE panel models so the
/// overlay can decide which monitor to host the panel kiosk window on.
/// </summary>
internal static class PanelDisplay
{
    // EDID names returned by EnumDisplayDevicesW are usually just "Generic PnP
    // Monitor" — Windows doesn't surface the real friendly name through that
    // API. HYTE panels are identified instead by the hardware DeviceID, which
    // is stable per panel model.
    private static readonly string[] KnownDeviceIdPrefixes =
    {
        @"MONITOR\RTK0004", // HYTE Y70ti / Y70 Touch (Realtek panel controller)
    };

    /// <summary>
    /// Returns the <see cref="MonitorInfo"/> of the first connected display
    /// whose hardware DeviceID prefix or EDID friendly name matches a known
    /// HYTE panel; null if none match.
    /// </summary>
    public static MonitorInfo? Find()
    {
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return null;

        foreach (var m in monitors)
        {
            if (string.IsNullOrEmpty(m.DeviceName)) continue;

            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(m.DeviceName, 0, ref dd, 0))
            {
                Log.Info($"panel display probe index={m.Index} device='{m.DeviceName}': EnumDisplayDevicesW false");
                continue;
            }

            var deviceId = dd.DeviceID ?? string.Empty;
            Log.Info($"panel display probe index={m.Index} device='{m.DeviceName}' devID='{deviceId}'");

            foreach (var prefix in KnownDeviceIdPrefixes)
            {
                if (deviceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Info($"panel display MATCHED devID prefix='{prefix}' index={m.Index}");
                    return m;
                }
            }
        }

        Log.Info("panel display: no monitor matched any known DeviceID prefix");
        return null;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
}
