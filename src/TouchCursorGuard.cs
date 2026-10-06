using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Stops a tap on a panel-kiosk touchscreen from stranding the desktop cursor
/// on it. Touching a touchscreen whose monitor differs from the cursor's makes
/// Windows teleport the single shared cursor to the contact point via
/// SetCursorPos. That move carries no mouse message (a WH_MOUSE_LL hook never
/// sees it - verified on the Y70), so it can't be blocked; it can only be
/// undone. WebView2 hides the cursor on touch, so the teleport is invisible
/// until the user moves the physical mouse, when the pointer reappears under
/// the last tap instead of where they left it.
///
/// Two hooks, both event-driven:
///   * WH_MOUSE_LL records the physical mouse position. Touch generates no
///     mouse events, so <see cref="_lastMousePt"/> only ever reflects the real
///     mouse - the signal that tells a genuine mouse move onto the panel from a
///     touch teleport.
///   * EVENT_OBJECT_LOCATIONCHANGE on the cursor fires when the cursor moves
///     (including the SetCursorPos teleport). When the cursor lands on a kiosk
///     monitor but the physical mouse is not there, snap it back to the mouse.
///
/// A mouse event that arrives while a touch has the cursor on the panel
/// reports a point offset from the touch, not from the mouse, so it does not
/// move <see cref="_lastMousePt"/>. When a display change leaves no other
/// monitor to return to (the others went to sleep), the guard remembers where
/// the mouse was and returns the cursor there once a monitor comes back.
/// <see cref="KeepOffBounds"/> (the Y70 "keep the mouse off" setting) also
/// stops the mouse itself from entering that panel.
///
/// The hooks are global, so they live only while a kiosk is open (Acquire /
/// Release ref-count). Every entry point and both callbacks run on the overlay
/// message-loop thread.
/// </summary>
internal static unsafe class TouchCursorGuard
{
    private static IntPtr _mouseHook;
    private static IntPtr _winEventHook;
    private static int _refCount;
    private static Native.POINT _lastMousePt;
    // Whether _lastMousePt was on a kiosk when recorded. Kept as a flag because
    // a display change moves the kiosk bounds the point would be tested against.
    private static bool _mouseOnKiosk;
    private static Native.POINT? _strandedFrom;
    private static Native.RECT? _keepOffBounds;

    /// <summary>Monitor rect the mouse is kept off, or null when the setting is off.</summary>
    public static Native.RECT? KeepOffBounds
    {
        get => _keepOffBounds;
        set
        {
            if (Same(_keepOffBounds, value)) return;
            _keepOffBounds = value;
            Log.Info(value is { } b
                ? $"touch-cursor-guard keeping the mouse off {b.Left},{b.Top},{b.Width}x{b.Height}"
                : "touch-cursor-guard keep-off cleared");
        }
    }

    public static void Acquire()
    {
        if (++_refCount != 1)
        {
            // A kiosk opened under a held guard: a mouse still resting at its
            // recorded point may now be on it. A cursor anywhere else was moved
            // by something other than the mouse, so the flag stays.
            if (!_mouseOnKiosk && Native.GetCursorPos(out var c) && c.x == _lastMousePt.x && c.y == _lastMousePt.y)
                _mouseOnKiosk = InKiosk(c);
            return;
        }
        Native.GetCursorPos(out _lastMousePt);
        _mouseOnKiosk = InKiosk(_lastMousePt);
        _strandedFrom = null;
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, &MouseProc,
            Native.GetModuleHandleW(null), 0);
        _winEventHook = Native.SetWinEventHook(
            Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, &CursorMoveProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        if (_mouseHook == IntPtr.Zero || _winEventHook == IntPtr.Zero)
            Log.Error($"touch-cursor-guard install failed mouse=0x{_mouseHook:X} winEvent=0x{_winEventHook:X} err={Marshal.GetLastWin32Error()}");
        else
            Log.Info("touch-cursor-guard installed");
    }

    public static void Release()
    {
        if (_refCount == 0 || --_refCount != 0) return;
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        Log.Info("touch-cursor-guard removed");
    }

    private static bool Same(Native.RECT? a, Native.RECT? b) =>
        a is { } x ? b is { } y && x.Left == y.Left && x.Top == y.Top && x.Right == y.Right && x.Bottom == y.Bottom : b is null;

    private static bool InKiosk(Native.POINT p) => PanelKioskWindow.PointInAnyKiosk(p.x, p.y);

    /// <summary>
    /// True when <paramref name="p"/> is on the kept-off panel. Checks the live
    /// monitor at the point as well, so bounds left stale by a display change
    /// never wall off part of another monitor.
    /// </summary>
    private static bool OnKeepOffPanel(Native.POINT p)
    {
        if (_keepOffBounds is not { } b) return false;
        if (p.x < b.Left || p.x >= b.Right || p.y < b.Top || p.y >= b.Bottom) return false;
        var monitor = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero) return false;
        var info = new Native.MonitorInfoNative { cbSize = Marshal.SizeOf<Native.MonitorInfoNative>() };
        return Native.GetMonitorInfoW(monitor, ref info) && Same(info.rcMonitor, b);
    }

    /// <summary>
    /// True when <paramref name="p"/> is a place to return the cursor to: on a
    /// live monitor, off every kiosk, and not on the monitor the cursor is on
    /// now. The last check covers kiosk bounds that are stale for the few
    /// seconds between a display change and the kiosk's recreate.
    /// </summary>
    private static bool IsReturnTarget(Native.POINT p, Native.POINT cursor)
    {
        if (InKiosk(p)) return false;
        var monitor = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONULL);
        return monitor != IntPtr.Zero
            && monitor != Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST);
    }

    private static Native.POINT PrimaryCenter() => new()
    {
        x = Native.GetSystemMetrics(Native.SM_CXSCREEN) / 2,
        y = Native.GetSystemMetrics(Native.SM_CYSCREEN) / 2,
    };

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == Native.HC_ACTION && lParam != IntPtr.Zero)
            {
                var pt = ((Native.MSLLHOOKSTRUCT*)lParam)->pt;
                var ptOnKiosk = InKiosk(pt);
                if ((ptOnKiosk || _keepOffBounds is not null) && Native.GetCursorPos(out var c))
                {
                    if (wParam == Native.WM_MOUSEMOVE && OnKeepOffPanel(pt) && !OnKeepOffPanel(c))
                        return 1; // the panel is a wall; the cursor stays where it is
                    if (ptOnKiosk && InKiosk(c) && !_mouseOnKiosk && _strandedFrom is null
                        && IsReturnTarget(_lastMousePt, c))
                        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
                }
                _lastMousePt = pt;
                _mouseOnKiosk = ptOnKiosk;
                if (!ptOnKiosk) _strandedFrom = null;
            }
        }
        catch { /* an exception must never cross back into the native hook chain */ }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void CursorMoveProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (idObject != Native.OBJID_CURSOR) return;
            if (!Native.GetCursorPos(out var c)) return;
            var keepOff = OnKeepOffPanel(c);
            if (!keepOff && !InKiosk(c)) return;
            var mouseOnPanel = _mouseOnKiosk && _strandedFrom is null;
            if (mouseOnPanel && !keepOff) return;

            var origin = _strandedFrom ?? _lastMousePt;
            var target = IsReturnTarget(origin, c) ? origin : PrimaryCenter();
            if (IsReturnTarget(target, c))
            {
                if (_strandedFrom is not null) Log.Info($"touch-cursor-guard returned the cursor to {target.x},{target.y}");
                _strandedFrom = null;
                _lastMousePt = target;
                _mouseOnKiosk = false;
                Native.SetCursorPos(target.x, target.y);
            }
            else if (!mouseOnPanel && _strandedFrom is null)
            {
                _strandedFrom = origin;
                Log.Info($"touch-cursor-guard no monitor to return to; holding {origin.x},{origin.y}");
            }
        }
        catch { /* an exception must never cross back into the native event callback */ }
    }
}
