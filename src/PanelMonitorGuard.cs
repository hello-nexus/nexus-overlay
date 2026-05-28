using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Keeps the HYTE panel monitor exclusive to the kiosk window. Windows still
/// treats the panel as an ordinary monitor (it reports a normal EDID over
/// DisplayPort), so the OS will happily extend the desktop onto it and let
/// other apps open or be dragged there — landing them behind the always-
/// topmost kiosk. This guard is the software answer: while the kiosk is up,
/// any *foreign* top-level application window that comes to rest on the panel
/// monitor is relocated back onto a normal monitor.
///
/// It is a guard, not an OS-level "specialized display" guarantee. Hooked via
/// <c>SetWinEventHook</c> (OUTOFCONTEXT) on the overlay's message-loop thread,
/// so callbacks run single-threaded with the rest of the overlay — no locking.
///
/// We deliberately watch only "window came to rest" events — foreground
/// changes, the end of a move/size drag, and window show — never the
/// high-frequency <c>EVENT_OBJECT_LOCATIONCHANGE</c>. Acting on
/// location-change would yank a window out from under the cursor mid-drag and
/// spin the CPU; reacting at drag-end is both calmer and what the user means
/// by "don't leave it there."
/// </summary>
internal sealed unsafe class PanelMonitorGuard : IDisposable
{
    // There is at most one kiosk, hence at most one guard. The WinEvent and
    // EnumWindows callbacks must be static [UnmanagedCallersOnly] for AOT, so
    // they route through this single static reference rather than a per-
    // instance managed delegate (which NativeAOT cannot marshal).
    private static PanelMonitorGuard? _current;

    private readonly IntPtr _kioskHwnd;
    private readonly IntPtr _panelMonitor;       // HMONITOR of the panel display
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

    private PanelMonitorGuard(IntPtr kioskHwnd, IntPtr panelMonitor, Native.RECT fallbackWork, bool hasFallback)
    {
        _kioskHwnd = kioskHwnd;
        _panelMonitor = panelMonitor;
        _fallbackWork = fallbackWork;
        _hasFallback = hasFallback;
        _ownProcessId = (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Begin guarding the monitor the kiosk window sits on. Picks a relocation
    /// target (the OS primary, else the first other monitor). If the panel is
    /// the only display there is nowhere to send evicted windows, so the guard
    /// installs nothing and simply no-ops — the topmost kiosk still hides them.
    /// </summary>
    public static PanelMonitorGuard Start(IntPtr kioskHwnd, MonitorInfo panel)
    {
        var panelMon = Native.MonitorFromWindow(kioskHwnd, Native.MONITOR_DEFAULTTONEAREST);

        var monitors = Monitors.Enumerate();
        MonitorInfo? fallback = null;
        foreach (var m in monitors)
            if (m.Index != panel.Index && m.Primary) { fallback = m; break; }
        if (fallback is null)
            foreach (var m in monitors)
                if (m.Index != panel.Index) { fallback = m; break; }

        var guard = new PanelMonitorGuard(
            kioskHwnd, panelMon, fallback?.WorkArea ?? default, fallback is not null);
        _current = guard;

        if (!guard._hasFallback)
        {
            Log.Warn($"panel-guard: panel monitor (index={panel.Index}) is the only display; nothing to evict onto, guard idle");
            return guard;
        }

        guard.InstallHooks();
        guard.SweepExisting();
        Log.Info($"panel-guard started panelMon=0x{panelMon:X} fallbackWork={guard._fallbackWork.Left},{guard._fallbackWork.Top} {guard._fallbackWork.Width}x{guard._fallbackWork.Height}");
        return guard;
    }

    private void InstallHooks()
    {
        const uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
        // One hook spans the small SYSTEM band (foreground .. move/size-end);
        // a second covers OBJECT_SHOW. The callback narrows to the exact three
        // events we act on — the in-between IDs (menu/capture/move-start) are
        // delivered too but ignored, which is cheaper than three separate hooks.
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
        // Whole-window events only — drop carets, cursors, scrollbars, and
        // child-control sub-objects that share the OBJECT event band.
        if (idObject != Native.OBJID_WINDOW || idChild != Native.CHILDID_SELF) return;
        // Act only on "window came to rest" events; ignore the other IDs in
        // the hooked SYSTEM band (move/size-start, menu, capture, …).
        if (ev != Native.EVENT_SYSTEM_FOREGROUND
            && ev != Native.EVENT_SYSTEM_MOVESIZEEND
            && ev != Native.EVENT_OBJECT_SHOW) return;

        var g = _current;
        if (g is null || g._disposed) return;
        try { g.EvaluateAndEvict(hwnd); }
        catch (Exception ex) { Log.Error($"panel-guard OnWinEvent: {ex.Message}"); }
    }

    /// <summary>Evict anything already sitting on the panel when the guard
    /// starts (e.g. an app the user left maximized there before the kiosk
    /// launched).</summary>
    private void SweepExisting()
    {
        Native.EnumWindows(&OnEnumWindow, IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        var g = _current;
        if (g is not null && !g._disposed)
        {
            try { g.EvaluateAndEvict(hwnd); }
            catch (Exception ex) { Log.Error($"panel-guard sweep: {ex.Message}"); }
        }
        return 1; // TRUE — continue enumeration
    }

    private void EvaluateAndEvict(IntPtr hwnd)
    {
        if (!_hasFallback) return;
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero) root = hwnd;
        if (root == _kioskHwnd) return;
        if (!ShouldEvict(root)) return;
        // Is the window actually on the panel? Use the same nearest-monitor
        // rule Windows uses to assign a window to a display, so we agree with
        // the OS about which monitor "owns" it.
        if (Native.MonitorFromWindow(root, Native.MONITOR_DEFAULTTONEAREST) != _panelMonitor) return;
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
        // desktop) — relocating an invisible window is wrong and pointless.
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
        return true;
    }

    private void Relocate(IntPtr hwnd)
    {
        if (Native.IsZoomed(hwnd))
        {
            // Maximized: move via placement so it re-maximizes on the fallback
            // monitor. A maximized window snaps to whichever monitor its
            // restored (rcNormalPosition) rect sits on, so aim that at the
            // fallback work area.
            var wp = new Native.WINDOWPLACEMENT { length = (uint)sizeof(Native.WINDOWPLACEMENT) };
            if (!Native.GetWindowPlacement(hwnd, ref wp)) return;
            wp.rcNormalPosition = CenteredHalf(_fallbackWork);
            wp.showCmd = (uint)Native.SW_SHOWMAXIMIZED;
            Native.SetWindowPlacement(hwnd, ref wp);
            Log.Info($"panel-guard relocated maximized hwnd=0x{hwnd:X}");
            return;
        }

        Native.GetWindowRect(hwnd, out var r);
        var (left, top) = PanelGuardGeometry.ClampTopLeft(
            r.Left, r.Top, r.Width, r.Height,
            _fallbackWork.Left, _fallbackWork.Top, _fallbackWork.Right, _fallbackWork.Bottom);
        Native.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Log.Info($"panel-guard relocated hwnd=0x{hwnd:X} -> {left},{top}");
    }

    // A rect half the work area's size, centered in it — a safe restored
    // position for a window we're re-maximizing onto the fallback monitor.
    private static Native.RECT CenteredHalf(Native.RECT work)
    {
        int w = work.Width / 2, h = work.Height / 2;
        int left = work.Left + w / 2, top = work.Top + h / 2;
        return new Native.RECT { Left = left, Top = top, Right = left + w, Bottom = top + h };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // UnhookWinEvent must run on the thread that called SetWinEventHook —
        // both happen on the message-loop thread (kiosk ctor / kiosk dispose).
        if (_hookSystem != IntPtr.Zero) { Native.UnhookWinEvent(_hookSystem); _hookSystem = IntPtr.Zero; }
        if (_hookObject != IntPtr.Zero) { Native.UnhookWinEvent(_hookObject); _hookObject = IntPtr.Zero; }
        if (ReferenceEquals(_current, this)) _current = null;
        Log.Info("panel-guard disposed");
    }
}
