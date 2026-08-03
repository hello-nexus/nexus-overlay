using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Hides the taskbar that lives on a see-through kiosk's monitor, and puts it
/// back. A kiosk window is topmost and the taskbar is too, so an opaque kiosk
/// merely covers it - but a transparent one shows it through the panel.
///
/// The shell's own full-screen handling is no use here: it lowers the taskbar
/// for a window that is ACTIVE, and the kiosk is WS_EX_NOACTIVATE precisely so
/// a touch never steals focus. So the window is hidden directly, which
/// survives our own death - hence <see cref="RestoreAll"/> at startup.
/// </summary>
internal sealed unsafe class PanelTaskbarGuard : IDisposable
{
    // Secondary bars only. Shell_TrayWnd is the system's one main taskbar,
    // carrying Start and the notification area; hiding it because a panel
    // happens to sit on the primary monitor takes away UI the user has no
    // other route to.
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";
    private const string PrimaryClass = "Shell_TrayWnd";

    // EnumWindows callbacks must be static [UnmanagedCallersOnly] for AOT, so
    // the scan target travels through these statics rather than a captured
    // instance. Scans are short and run on the overlay's message-loop thread.
    private static readonly object ScanLock = new();
    private static IntPtr _scanMonitor;
    private static List<IntPtr>? _scanHits;

    private IntPtr _monitor;
    private readonly List<IntPtr> _hidden = new();
    private bool _disposed;

    private PanelTaskbarGuard(IntPtr monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Hide the secondary taskbar on the kiosk's monitor, if there is one.
    /// Returns null only when the window has no monitor; otherwise the guard
    /// is live so a taskbar that appears later still gets hidden.
    /// </summary>
    public static PanelTaskbarGuard? Start(IntPtr kioskHwnd)
    {
        var monitor = Native.MonitorFromWindow(kioskHwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return null;

        var guard = new PanelTaskbarGuard(monitor);
        guard.Reassert();
        return guard;
    }

    /// <summary>Re-target after the kiosk moved to different bounds; the
    /// captured monitor handle is stale after a rotation or an arrangement
    /// change, and a stale one silently matches nothing.</summary>
    public void Retarget(IntPtr kioskHwnd)
    {
        if (_disposed) return;
        var monitor = Native.MonitorFromWindow(kioskHwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || monitor == _monitor) return;
        RestoreHidden();
        _monitor = monitor;
        Reassert();
    }

    /// <summary>
    /// Re-hide anything the shell brought back. Explorer restores its taskbars
    /// on restart and on display changes, so the owner calls this from its
    /// existing reconcile poll rather than hooking TaskbarCreated.
    /// </summary>
    public void Reassert()
    {
        if (_disposed) return;
        foreach (var hwnd in Scan(_monitor))
        {
            if (!Native.IsWindowVisible(hwnd)) continue;
            // ShowWindow returns the PREVIOUS visibility, not success, so the
            // post-check is what tells us whether the hide took.
            Native.ShowWindow(hwnd, Native.SW_HIDE);
            if (Native.IsWindowVisible(hwnd))
            {
                Log.Warn($"panel-taskbar hide did not take for hwnd=0x{hwnd:X}");
                continue;
            }
            if (!_hidden.Contains(hwnd)) _hidden.Add(hwnd);
            Log.Info($"panel-taskbar hid secondary taskbar hwnd=0x{hwnd:X} on the kiosk monitor");
        }
    }

    /// <summary>
    /// Show every taskbar on every monitor. Hiding another process's window
    /// outlives this process, so a crash while a see-through kiosk was up
    /// would otherwise leave the user with no taskbar until Explorer restarts.
    /// Showing an already-visible window is a no-op, so this is safe to run
    /// unconditionally at startup.
    /// </summary>
    public static void RestoreAll()
    {
        foreach (var hwnd in Scan(IntPtr.Zero))
        {
            if (Native.IsWindowVisible(hwnd)) continue;
            Native.ShowWindow(hwnd, Native.SW_SHOW);
            Log.Info($"panel-taskbar restored taskbar hwnd=0x{hwnd:X}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreHidden();
    }

    /// <summary>
    /// Show what this guard hid. Each handle is re-checked by class first:
    /// Explorer restarts are routine here, HWNDs are recycled, and showing a
    /// recycled handle would un-hide some unrelated window.
    /// </summary>
    private void RestoreHidden()
    {
        foreach (var hwnd in _hidden)
        {
            if (!Native.IsWindow(hwnd) || !IsTaskbar(hwnd)) continue;
            Native.ShowWindow(hwnd, Native.SW_SHOW);
        }
        _hidden.Clear();
    }

    private static bool IsTaskbar(IntPtr hwnd)
    {
        Span<char> cls = stackalloc char[64];
        int n;
        fixed (char* p = cls) n = Native.GetClassNameW(hwnd, p, cls.Length);
        if (n <= 0) return false;
        var name = cls.Slice(0, n);
        return name.SequenceEqual(SecondaryClass) || name.SequenceEqual(PrimaryClass);
    }

    /// <summary>Taskbar windows on <paramref name="monitor"/>, or on every
    /// monitor when it is zero.</summary>
    private static List<IntPtr> Scan(IntPtr monitor)
    {
        lock (ScanLock)
        {
            _scanMonitor = monitor;
            _scanHits = new List<IntPtr>();
            Native.EnumWindows(&OnEnumWindow, IntPtr.Zero);
            var hits = _scanHits;
            _scanHits = null;
            return hits;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        var hits = _scanHits;
        if (hits is null) return 0;

        try
        {
            Span<char> cls = stackalloc char[64];
            int n;
            fixed (char* p = cls) n = Native.GetClassNameW(hwnd, p, cls.Length);
            if (n <= 0) return 1;
            var name = cls.Slice(0, n);
            // RestoreAll (monitor zero) also sweeps the primary bar, so a build
            // that once hid it recovers; the hide path only ever takes the
            // secondary one.
            var match = _scanMonitor == IntPtr.Zero
                ? name.SequenceEqual(SecondaryClass) || name.SequenceEqual(PrimaryClass)
                : name.SequenceEqual(SecondaryClass);
            if (!match) return 1;
            if (_scanMonitor != IntPtr.Zero
                && Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) != _scanMonitor)
            {
                return 1;
            }
            hits.Add(hwnd);
        }
        catch
        {
            // A managed exception unwinding into EnumWindows' native frames
            // fail-fasts the process under AOT.
        }
        return 1;
    }
}
