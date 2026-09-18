using System;
using System.Collections.Generic;
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

    internal const uint DISPLAY_DEVICE_ACTIVE = 0x00000001;

    /// <summary>Stable id for the monitor child a GDI adapter is driving.</summary>
    public static string ResolveStableId(string adapterDeviceName)
    {
        if (string.IsNullOrEmpty(adapterDeviceName)) return "";
        if (!TryGetDrivenMonitor(adapterDeviceName, EDD_GET_DEVICE_INTERFACE_NAME, out var dd)
            && !TryGetDrivenMonitor(adapterDeviceName, 0, out dd))
        {
            // Total API failure: the service's ResolveIdentity publishes the
            // raw adapter name as the id in this case - return the same
            // value so an assignment minted against it still matches.
            return adapterDeviceName;
        }
        return ExtractStableId(dd.DeviceID ?? "", adapterDeviceName);
    }

    /// <summary>
    /// Raw PnP DeviceID of the monitor child the adapter is driving; empty
    /// when the API fails. Same child rule as ResolveStableId.
    /// </summary>
    public static string ReadDrivenMonitorDeviceId(string adapterDeviceName)
        => TryGetDrivenMonitor(adapterDeviceName, 0, out var dd) ? dd.DeviceID ?? "" : "";

    /// <summary>
    /// Which of an adapter's monitor children it is driving: the first one
    /// flagged DISPLAY_DEVICE_ACTIVE, else child 0 (a driver that never sets
    /// the flag keeps working), -1 for no children. Windows lists every
    /// plugged-in devnode as a child, including ones off the desktop, in PnP
    /// enumeration order, so child 0 is not necessarily the driven monitor.
    /// Mirrors nexus-service's MonitorChildSelection.Pick.
    /// </summary>
    public static int PickDrivenChild(IReadOnlyList<uint> childStateFlags)
    {
        for (var i = 0; i < childStateFlags.Count; i++)
        {
            if ((childStateFlags[i] & DISPLAY_DEVICE_ACTIVE) != 0) return i;
        }
        return childStateFlags.Count == 0 ? -1 : 0;
    }

    private static bool TryGetDrivenMonitor(string adapterDeviceName, uint flags, out DISPLAY_DEVICE monitor)
    {
        var children = new List<DISPLAY_DEVICE>();
        var flagsOnly = new List<uint>();
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(adapterDeviceName, i, ref dd, flags)) break;
            children.Add(dd);
            flagsOnly.Add(dd.StateFlags);
        }
        var pick = PickDrivenChild(flagsOnly);
        if (pick < 0)
        {
            monitor = default;
            return false;
        }
        monitor = children[pick];
        return true;
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
