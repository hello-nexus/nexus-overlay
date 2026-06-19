using System;
using System.Runtime.InteropServices;

namespace Nexus.Overlay;

/// <summary>
/// Resolves a GDI adapter device name (\\.\DISPLAYn) to the EDID-stable
/// display id the service uses as the monitor-panel assignment key. The
/// extraction must stay byte-identical to WindowsDisplayIdentity in
/// nexus-service - a separate assembly, so the algorithm is duplicated;
/// keep the two in sync (same precedent as PanelDisplay's hardware names).
/// </summary>
internal static class DisplayIdentity
{
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

    /// <summary>Stable id for the first monitor child of a GDI adapter.</summary>
    public static string ResolveStableId(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return "";
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME))
        {
            dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDeviceName, 0, ref dd, 0))
            {
                // Total API failure: the service's ResolveIdentity publishes the
                // raw adapter name as the id in this case - return the same
                // value so an assignment minted against it still matches.
                return adapterDeviceName;
            }
        }
        return ExtractStableId(dd.DeviceID ?? "", adapterDeviceName);
    }

    /// <summary>
    /// Pure extraction: PnP DeviceID (\\?\DISPLAY#DEL41B7#5&abc#{guid}) ->
    /// sanitized EDID-stable id ("DEL41B7-5-abc"); adapter-tail fallback when
    /// the DeviceID is empty.
    /// </summary>
    public static string ExtractStableId(string deviceId, string adapterDeviceName)
    {
        var stable = deviceId;
        var firstHash = deviceId.IndexOf('#');
        var lastHash = deviceId.LastIndexOf('#');
        if (firstHash > 0 && lastHash > firstHash)
        {
            stable = deviceId.Substring(firstHash + 1, lastHash - firstHash - 1);
        }
        if (string.IsNullOrEmpty(stable))
        {
            var lastSlash = adapterDeviceName.LastIndexOf('\\');
            var tail = lastSlash >= 0 ? adapterDeviceName[(lastSlash + 1)..] : adapterDeviceName;
            stable = string.IsNullOrEmpty(tail) ? "display-unknown" : tail.ToLowerInvariant();
        }
        return SanitizeId(stable);
    }

    private static string SanitizeId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var buf = new char[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            buf[i] = (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') ? c : '-';
        }
        return new string(buf);
    }

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

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
