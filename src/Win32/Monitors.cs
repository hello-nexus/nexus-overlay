using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Qos.Overlay.Win32;

/// <summary>
/// Snapshot of physical monitors in <c>EnumDisplayMonitors</c> order. The
/// SPA reads the <c>?monitor=N</c> query string and filters
/// <c>desktopLayout</c> entries by it; persistence stores the same index.
/// On display change the host re-enumerates and recreates overlays as
/// needed.
/// </summary>
internal sealed class MonitorInfo
{
    public int Index { get; init; }
    public Native.Rect Bounds { get; init; }
    public Native.Rect WorkArea { get; init; }
    public bool Primary { get; init; }
}

internal static class Monitors
{
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var collected = new List<MonitorInfo>();
        var callbackInvocations = 0;

        Native.MonitorEnumProc proc = OnMonitor;
        var ok = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        Qos.Overlay.Log.Info($"EnumDisplayMonitors returned ok={ok} callbacks={callbackInvocations}");

        // Fallback: if EnumDisplayMonitors didn't surface anything (rare,
        // sometimes happens when the GDI session is in an odd state right
        // after logon), construct a single virtual screen rect from
        // SystemInformation. Better one rectangle than zero overlays.
        if (collected.Count == 0)
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is not null)
            {
                Qos.Overlay.Log.Warn("falling back to System.Windows.Forms.Screen.PrimaryScreen");
                collected.Add(new MonitorInfo
                {
                    Index = 0,
                    Bounds = new Native.Rect
                    {
                        Left = screen.Bounds.Left,
                        Top = screen.Bounds.Top,
                        Right = screen.Bounds.Right,
                        Bottom = screen.Bounds.Bottom,
                    },
                    WorkArea = new Native.Rect
                    {
                        Left = screen.WorkingArea.Left,
                        Top = screen.WorkingArea.Top,
                        Right = screen.WorkingArea.Right,
                        Bottom = screen.WorkingArea.Bottom,
                    },
                    Primary = true,
                });
            }
        }

        return collected;

        bool OnMonitor(IntPtr hMonitor, IntPtr hdcMonitor, ref Native.Rect lprcMonitor, IntPtr dwData)
        {
            callbackInvocations++;
            var info = new Native.MonitorInfo
            {
                cbSize = Marshal.SizeOf<Native.MonitorInfo>(),
            };
            if (Native.GetMonitorInfo(hMonitor, ref info))
            {
                collected.Add(new MonitorInfo
                {
                    Index = collected.Count,
                    Bounds = info.rcMonitor,
                    WorkArea = info.rcWork,
                    Primary = (info.dwFlags & 1) != 0,
                });
            }
            return true;
        }
    }
}
