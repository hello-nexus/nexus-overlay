using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Keeps the HYTE panel monitor exclusive to the kiosk window. The panel
/// reports a normal EDID over DisplayPort, so Windows treats it as an ordinary
/// monitor and lets other apps open or be dragged there, behind the always-
/// topmost kiosk. While the kiosk is up, any foreign top-level window that
/// comes to rest on the panel monitor is relocated back onto a normal monitor.
///
/// Hooked via <c>SetWinEventHook</c> (OUTOFCONTEXT) on the overlay's
/// message-loop thread, so callbacks run single-threaded with the rest of the
/// overlay - no locking.
///
/// Watches only "window came to rest" events - foreground changes, move/size
/// drag-end, and window show - never the high-frequency
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c>: acting on location-change would yank a
/// window out from under the cursor mid-drag and spin the CPU.
/// </summary>
internal sealed unsafe class PanelMonitorGuard : IDisposable
{
    // One guard per kiosk window (Y70 kiosk + each promoted-monitor kiosk).
    // The WinEvent and EnumWindows callbacks must be static
    // [UnmanagedCallersOnly] for AOT, so they iterate this registry rather
    // than a per-instance managed delegate (which NativeAOT cannot marshal).
    // Each guard owns a distinct monitor, so a window is relocated by at
    // most one of them; with N guards an event is evaluated N×N times, all
    // cheap monitor-membership checks (N is the panel count, single digits).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, PanelMonitorGuard> _active = new();
    private static int _nextGuardId;
    private readonly int _guardId;

    // While the session is locked, Windows parks lock-experience windows on
    // every monitor, the panel included; evicting one drops a panel-sized
    // band over the primary display's own lock screen. Written and read only
    // on the message-loop thread (WinEvent callbacks, the EnumWindows sweep,
    // and WM_WTSSESSION_CHANGE all arrive there).
    private static bool _sessionLocked;

    private readonly IntPtr _kioskHwnd;
    private readonly IntPtr _panelMonitor;       // HMONITOR of the panel display
    private readonly Native.RECT _panelBounds;   // for excluding this monitor as another guard's fallback
    private readonly uint _ownProcessId;
    private readonly bool _hasFallback;
    private readonly Native.RECT _fallbackWork;  // work area to relocate windows into
    private IntPtr _hookSystem;
    private IntPtr _hookObject;
    private bool _disposed;

    // Shell-owned surfaces that must never be relocated. Moving the desktop,
    // a taskbar, the Start/Search/Task-View UI, or a transient menu/tooltip
    // either breaks the shell or produces a visible glitch; the shell also
    // pins most of these to the primary monitor regardless.
    private static readonly string[] ShellClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "ForegroundStaging", "MultitaskingViewFrame",
        "#32768" /* menus */, "tooltips_class32", "ComboLBox",
    };

    private PanelMonitorGuard(IntPtr kioskHwnd, IntPtr panelMonitor, Native.RECT panelBounds, Native.RECT fallbackWork, bool hasFallback)
    {
        _guardId = Interlocked.Increment(ref _nextGuardId);
        _kioskHwnd = kioskHwnd;
        _panelMonitor = panelMonitor;
        _panelBounds = panelBounds;
        _fallbackWork = fallbackWork;
        _hasFallback = hasFallback;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Begin guarding the monitor the kiosk window sits on. Picks a relocation
    /// target (the OS primary, else the first other monitor). If the panel is
    /// the only display there is nowhere to send evicted windows, so the guard
    /// installs nothing and no-ops.
    /// </summary>
    public static PanelMonitorGuard Start(IntPtr kioskHwnd, MonitorInfo panel)
    {
        var panelMon = Native.MonitorFromWindow(kioskHwnd, Native.MONITOR_DEFAULTTONEAREST);

        // Never relocate onto a monitor another guard already owns - that
        // would shove windows under a topmost kiosk (and the guards would
        // bounce them between each other).
        var monitors = Monitors.Enumerate();
        MonitorInfo? fallback = null;
        foreach (var m in monitors)
            if (m.Index != panel.Index && m.Primary && !IsGuardedBounds(m.Bounds)) { fallback = m; break; }
        if (fallback is null)
            foreach (var m in monitors)
                if (m.Index != panel.Index && !IsGuardedBounds(m.Bounds)) { fallback = m; break; }

        var guard = new PanelMonitorGuard(
            kioskHwnd, panelMon, panel.Bounds, fallback?.WorkArea ?? default, fallback is not null);
        _active[guard._guardId] = guard;

        if (!guard._hasFallback)
        {
            Log.Warn($"panel-guard: no unguarded display to evict onto (panel index={panel.Index}); guard idle");
            return guard;
        }

        guard.InstallHooks();
        SweepAll();
        Log.Info($"panel-guard started panelMon=0x{panelMon:X} fallbackWork={guard._fallbackWork.Left},{guard._fallbackWork.Top} {guard._fallbackWork.Width}x{guard._fallbackWork.Height}");
        return guard;
    }

    /// <summary>Seed <see cref="_sessionLocked"/> at process start. WTS
    /// notifications deliver only transitions, so a guard created while the
    /// session is already locked (service respawn, kiosk watchdog recreate)
    /// would otherwise sweep lock windows off the panel.</summary>
    public static void InitializeSessionLockState()
    {
        _sessionLocked = QuerySessionLocked();
        if (_sessionLocked) Log.Info("panel-guard: session locked at startup; evictions suspended");
    }

    /// <summary>Handles WM_WTSSESSION_CHANGE (forwarded by the marshaler
    /// window). Unlock re-sweeps every guard so a window that landed on a
    /// panel during the locked span is relocated once it matters.</summary>
    public static void OnSessionChange(IntPtr wParam)
    {
        switch ((int)wParam)
        {
            case Native.WTS_SESSION_LOCK:
                _sessionLocked = true;
                Log.Info("panel-guard: session locked; evictions suspended");
                break;
            case Native.WTS_SESSION_UNLOCK:
                _sessionLocked = false;
                Log.Info("panel-guard: session unlocked; evictions resumed");
                if (!_active.IsEmpty)
                {
                    try { SweepAll(); }
                    catch (Exception ex) { Log.Error($"panel-guard unlock sweep: {ex.Message}"); }
                }
                break;
        }
    }

    private static bool QuerySessionLocked()
    {
        try
        {
            if (!Native.WTSQuerySessionInformationW(IntPtr.Zero, Native.WTS_CURRENT_SESSION,
                    Native.WTSSessionInfoEx, out var buf, out var len) || buf == IntPtr.Zero)
                return false;
            try
            {
                if (len < (uint)sizeof(Native.WTSINFOEX_PREFIX)) return false;
                var info = *(Native.WTSINFOEX_PREFIX*)buf;
                // UNKNOWN (0xFFFFFFFF) is treated as unlocked so a failed
                // query cannot suspend evictions permanently.
                return info.Level == 1 && info.SessionFlags == Native.WTS_SESSIONSTATE_LOCK;
            }
            finally { Native.WTSFreeMemory(buf); }
        }
        catch (Exception ex)
        {
            Log.Error($"panel-guard lock-state query: {ex.Message}");
            return false;
        }
    }

    private static bool IsGuardedBounds(Native.RECT bounds)
    {
        foreach (var kv in _active)
        {
            var g = kv.Value;
            if (g._disposed) continue;
            var p = g._panelBounds;
            if (p.Left == bounds.Left && p.Top == bounds.Top && p.Right == bounds.Right && p.Bottom == bounds.Bottom)
                return true;
        }
        return false;
    }

    private void InstallHooks()
    {
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        // One hook spans the SYSTEM band (foreground .. move/size-end), a
        // second covers OBJECT_SHOW. The callback acts on three events; the
        // in-between IDs (menu/capture/move-start) are delivered but ignored.
        _hookSystem = Native.SetWinEventHook(
            Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, &OnWinEvent, 0, 0, flags);
        _hookObject = Native.SetWinEventHook(
            Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW,
            IntPtr.Zero, &OnWinEvent, 0, 0, flags);
        Log.Info($"panel-guard hooks installed system=0x{_hookSystem:X} object=0x{_hookObject:X}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        // Whole-window events only - drop carets, cursors, scrollbars, and
        // child-control sub-objects that share the OBJECT event band.
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;
        // Act only on "window came to rest" events; ignore the other IDs in
        // the hooked SYSTEM band (move/size-start, menu, capture, …).
        if (ev != Native.EVENT_SYSTEM_FOREGROUND
            && ev != Native.EVENT_SYSTEM_MOVESIZEEND
            && ev != Native.EVENT_OBJECT_SHOW) return;

        foreach (var kv in _active)
        {
            var g = kv.Value;
            if (g._disposed) continue;
            try { g.EvaluateAndEvict(hwnd); }
            catch (Exception ex) { Log.Error($"panel-guard OnWinEvent: {ex.Message}"); }
        }
    }

    /// <summary>Evict anything already sitting on a guarded monitor (e.g. an
    /// app the user left maximized there before the kiosk launched). One
    /// EnumWindows pass; the callback evaluates every active guard.</summary>
    private static void SweepAll()
    {
        Native.EnumWindows(&OnEnumWindow, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        foreach (var kv in _active)
        {
            var g = kv.Value;
            if (g._disposed) continue;
            try { g.EvaluateAndEvict(hwnd); }
            catch (Exception ex) { Log.Error($"panel-guard sweep: {ex.Message}"); }
        }
        return 1; // TRUE - continue enumeration
    }

    private void EvaluateAndEvict(IntPtr hwnd)
    {
        if (!_hasFallback) return;
        if (_sessionLocked) return;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (root == _kioskHwnd) return;
        // Is the window actually on the panel? Use the same nearest-monitor
        // rule Windows uses to assign a window to a display, so we agree with
        // the OS about which monitor "owns" it. Checked before ShouldEvict:
        // that predicate ends in a process-image query only windows resting
        // on the panel should pay.
        if (Native.MonitorFromWindow(root, Native.MONITOR_DEFAULTTONEAREST) != _panelMonitor) return;
        if (!ShouldEvict(root)) return;
        Relocate(root);
    }

    private bool ShouldEvict(IntPtr hwnd)
    {
        // Foreign windows only. The hooks set SKIPOWNPROCESS, but the
        // EnumWindows sweep does not filter by process, so re-check cheaply.
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _ownProcessId) return false;

        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        if (Native.IsIconic(hwnd)) return false; // minimized: off-screen sentinel coords

        var exStyle = (uint)Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE);
        if ((exStyle & (uint)Native.WS_EX_TOOLWINDOW) != 0) return false; // helper/tray windows

        // Skip windows DWM is cloaking (suspended UWP, or on another virtual
        // desktop) - relocating an invisible window is wrong and pointless.
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        Span<char> cls = stackalloc char[64];
        int n;
        fixed (char* p = cls) n = Native.GetClassNameW(hwnd, p, cls.Length);
        if (n > 0)
        {
            var name = cls.Slice(0, n);
            foreach (var skip in ShellClasses)
                if (name.SequenceEqual(skip)) return false;
        }

        // The lock notification and the lock window's own SHOW event arrive
        // through the same message queue with no ordering guarantee, so
        // lock-experience processes are exempt regardless of _sessionLocked.
        if (IsLockScreenProcess(pid)) return false;
        return true;
    }

    private static bool IsLockScreenProcess(uint pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            Span<char> path = stackalloc char[512];
            uint len = (uint)path.Length;
            fixed (char* p = path)
            {
                if (!Native.QueryFullProcessImageNameW(h, 0, p, ref len)) return false;
            }
            var name = path.Slice(0, (int)len);
            int slash = name.LastIndexOf('\\');
            if (slash >= 0) name = name.Slice(slash + 1);
            return name.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LogonUI.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally { Native.CloseHandle(h); }
    }

    private void Relocate(IntPtr hwnd)
    {
        // SetWindowPlacement on an already-maximized window updates its stored
        // restore rect but does NOT move the maximized window to another
        // monitor - Windows only re-picks the maximize monitor across a
        // restore→maximize transition. So for a maximized window: restore it,
        // move the windowed frame onto the fallback, then re-maximize there.
        bool wasMaximized = Native.IsZoomed(hwnd);
        if (wasMaximized) Native.ShowWindow(hwnd, Native.SW_RESTORE);

        Native.GetWindowRect(hwnd, out var r);
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            r.Left, r.Top, r.Width, r.Height,
            _fallbackWork.Left, _fallbackWork.Top, _fallbackWork.Right, _fallbackWork.Bottom);
        Native.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

        if (wasMaximized)
        {
            Native.ShowWindow(hwnd, Native.SW_SHOWMAXIMIZED);
            Log.Info($"panel-guard relocated maximized hwnd=0x{hwnd:X} -> {left},{top}");
        }
        else
        {
            Log.Info($"panel-guard relocated hwnd=0x{hwnd:X} -> {left},{top}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // UnhookWinEvent must run on the thread that called SetWinEventHook -
        // both happen on the message-loop thread (kiosk ctor / kiosk dispose).
        if (_hookSystem != IntPtr.Zero) { Native.UnhookWinEvent(_hookSystem); _hookSystem = IntPtr.Zero; }
        if (_hookObject != IntPtr.Zero) { Native.UnhookWinEvent(_hookObject); _hookObject = IntPtr.Zero; }
        _active.TryRemove(_guardId, out _);
        Log.Info("panel-guard disposed");
    }
}
