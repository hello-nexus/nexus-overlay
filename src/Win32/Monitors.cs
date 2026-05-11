using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    public Native.RECT Bounds { get; init; }
    public Native.RECT WorkArea { get; init; }
    public bool Primary { get; init; }
}

internal static unsafe class Monitors
{
    // EnumDisplayMonitors callback cannot capture closures (must be a
    // static [UnmanagedCallersOnly] for AOT). Stash the collecting list
    // and a count in a thread-static so the callback can append.
    [ThreadStatic] private static List<MonitorInfo>? _collected;
    [ThreadStatic] private static int _invocations;

    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        _collected = new List<MonitorInfo>();
        _invocations = 0;
        try
        {
            var ok = Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, IntPtr.Zero);
            Log.Info($"EnumDisplayMonitors returned ok={ok} callbacks={_invocations}");

            // Fallback: if EnumDisplayMonitors didn't surface anything (rare,
            // sometimes happens when the GDI session is in an odd state right
            // after logon), construct a single virtual screen rect from
            // SystemMetrics. Better one rectangle than zero overlays.
            if (_collected.Count == 0)
            {
                var w = Native.GetSystemMetrics(Native.SM_CXSCREEN);
                var h = Native.GetSystemMetrics(Native.SM_CYSCREEN);
                if (w > 0 && h > 0)
                {
                    Log.Warn($"falling back to GetSystemMetrics primary screen {w}x{h}");
                    _collected.Add(new MonitorInfo
                    {
                        Index = 0,
                        Bounds = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h },
                        WorkArea = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h },
                        Primary = true,
                    });
                }
            }
            return _collected;
        }
        finally
        {
            _collected = null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnMonitor(IntPtr hMonitor, IntPtr hdcMonitor, Native.RECT* lprcMonitor, IntPtr dwData)
    {
        _invocations++;
        var info = new Native.MonitorInfoNative
        {
            cbSize = sizeof(Native.MonitorInfoNative),
        };
        if (Native.GetMonitorInfoW(hMonitor, ref info) && _collected is not null)
        {
            _collected.Add(new MonitorInfo
            {
                Index = _collected.Count,
                Bounds = info.rcMonitor,
                WorkArea = info.rcWork,
                Primary = (info.dwFlags & 1) != 0,
            });
        }
        return 1; // TRUE - continue enumeration
    }
}
