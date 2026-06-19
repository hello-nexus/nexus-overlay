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

    public static void Acquire()
    {
        if (++_refCount != 1) return;
        Native.GetCursorPos(out _lastMousePt);
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

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == Native.HC_ACTION && lParam != IntPtr.Zero)
                _lastMousePt = ((Native.MSLLHOOKSTRUCT*)lParam)->pt;
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
            if (PanelKioskWindow.PointInAnyKiosk(c.x, c.y)
                && !PanelKioskWindow.PointInAnyKiosk(_lastMousePt.x, _lastMousePt.y))
            {
                Native.SetCursorPos(_lastMousePt.x, _lastMousePt.y);
            }
        }
        catch { /* an exception must never cross back into the native event callback */ }
    }
}
