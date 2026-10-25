using System;
using System.Collections.Generic;
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
///     mouse events, so <see cref="CursorGuardMachine.LastMousePt"/> only ever reflects the real
///     mouse - the signal that tells a genuine mouse move onto the panel from a
///     touch teleport.
///   * EVENT_OBJECT_LOCATIONCHANGE on the cursor fires when the cursor moves
///     (including the SetCursorPos teleport). When the cursor lands on a kiosk
///     monitor but the physical mouse is not there, snap it back to the mouse.
///
/// A mouse event that arrives while a touch has the cursor on the panel
/// reports a point offset from the touch, not from the mouse, so it does not
/// move <see cref="CursorGuardMachine.LastMousePt"/>. Mouse input promoted from touch or pen
/// carries a signature in its extra info; the entry check below ignores it.
///
/// A mouse pushing past an edge, through the dead zone beside a kiosk or across
/// the gap between two kiosks is a case the hook point cannot explain: Windows
/// slides the cursor onto the kiosk beyond. A <see cref="MouseEntryTracker"/>
/// armed by such a push marks the slide as the mouse's own.
///
/// When a display change leaves no other monitor to return to (the others went to
/// sleep), the guard remembers where the mouse was and returns the cursor there once a monitor comes back.
/// <see cref="KeepOffBounds"/> (each panel's "keep the mouse off" setting)
/// also stops the mouse itself from entering those panels.
///
/// The decisions live in <see cref="CursorGuardMachine"/>; the callbacks here only
/// feed it and carry out what it returns. The hooks are global, so they live only
/// while a kiosk is open (Acquire / Release ref-count). Every entry point and both
/// callbacks run on the overlay message-loop thread.
/// </summary>
internal static unsafe class TouchCursorGuard
{
    private sealed class World : ICursorGuardWorld
    {
        public IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Monitors => _monitorRects;

        public IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Kiosks => KioskRectTuples();

        public bool HasKeepOff => _keepOffBounds.Length > 0;

        public bool OnKeepOffPanel((int X, int Y) p) => TouchCursorGuard.OnKeepOffPanel(new Native.POINT { x = p.X, y = p.Y });

        public bool MonitorStillAt((int X, int Y) p, (int Left, int Top, int Right, int Bottom) cachedRect)
        {
            var monitor = Native.MonitorFromPoint(new Native.POINT { x = p.X, y = p.Y }, Native.MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero) return false;
            var info = new Native.MonitorInfoNative { cbSize = Marshal.SizeOf<Native.MonitorInfoNative>() };
            return Native.GetMonitorInfoW(monitor, ref info)
                && info.rcMonitor.Left == cachedRect.Left && info.rcMonitor.Top == cachedRect.Top
                && info.rcMonitor.Right == cachedRect.Right && info.rcMonitor.Bottom == cachedRect.Bottom;
        }

        public void RefreshMonitors()
        {
            _refreshes++;
            RefreshMonitorRects();
        }
    }

    /// <summary>Raised on the overlay thread after the physical mouse moved the cursor; never for touch or pen.</summary>
    public static event Action? RealMouseMoved;

    // Set by the mouse hook, consumed by the next cursor-moved event: the hook runs before the move lands, and
    // ShowCursor does not belong inside a time-limited low-level hook.
    private static bool _realMouseMovePending;
    private static IntPtr _mouseHook;
    private static IntPtr _winEventHook;
    private static int _refCount;
    private static readonly CursorGuardMachine Machine = new(new World());
    private static Native.RECT[] _keepOffBounds = Array.Empty<Native.RECT>();
    // A push held against an edge can move the cursor once per mouse event.
    private const long MoveLogIntervalMs = 2000;
    private static long _nextMoveLogAt;
    private static int _unloggedMoves;
    private static int _refreshes;
    // Both callbacks read this cache; Acquire and OnDisplayChange refresh it.
    private static (int Left, int Top, int Right, int Bottom)[] _monitorRects = Array.Empty<(int, int, int, int)>();

    /// <summary>Monitor rects the mouse is kept off; empty when no panel has the setting on.</summary>
    public static Native.RECT[] KeepOffBounds
    {
        get => _keepOffBounds;
        set
        {
            if (SameAll(_keepOffBounds, value)) return;
            _keepOffBounds = value;
            Log.Info(value.Length == 0
                ? "touch-cursor-guard keep-off cleared"
                : $"touch-cursor-guard keeping the mouse off {string.Join("; ", Array.ConvertAll(value, b => $"{b.Left},{b.Top},{b.Width}x{b.Height}"))}");
        }
    }

    public static void Acquire()
    {
        if (++_refCount != 1)
        {
            if (Native.GetCursorPos(out var held)) Machine.KioskOpened((held.x, held.y));
            return;
        }
        RefreshMonitorRects();
        Native.GetCursorPos(out var c);
        Machine.Start((c.x, c.y));
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
        FlushMoveLog();
        Log.Info("touch-cursor-guard removed");
    }

    public static void OnDisplayChange()
    {
        if (_refCount == 0) return;
        RefreshMonitorRects();
        FlushMoveLog();
    }

    private static bool Same(Native.RECT a, Native.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static bool SameAll(Native.RECT[] a, Native.RECT[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (!Same(a[i], b[i])) return false;
        return true;
    }

    private static void RefreshMonitorRects()
    {
        var monitors = Monitors.Enumerate();
        var rects = new (int Left, int Top, int Right, int Bottom)[monitors.Count];
        for (var i = 0; i < rects.Length; i++)
        {
            var b = monitors[i].Bounds;
            rects[i] = (b.Left, b.Top, b.Right, b.Bottom);
        }
        _monitorRects = rects;
    }

    /// <summary>
    /// True when <paramref name="p"/> is on the kept-off panel. Checks the live
    /// monitor at the point as well, so bounds left stale by a display change
    /// never wall off part of another monitor.
    /// </summary>
    private static bool OnKeepOffPanel(Native.POINT p)
    {
        foreach (var b in _keepOffBounds)
        {
            if (p.x < b.Left || p.x >= b.Right || p.y < b.Top || p.y >= b.Bottom) continue;
            var monitor = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero) return false;
            var info = new Native.MonitorInfoNative { cbSize = Marshal.SizeOf<Native.MonitorInfoNative>() };
            if (Native.GetMonitorInfoW(monitor, ref info) && Same(info.rcMonitor, b)) return true;
        }
        return false;
    }

    private static void LogMove((int X, int Y) from, (int X, int Y) to, string reason)
    {
        var now = Environment.TickCount64;
        if (now < _nextMoveLogAt) { _unloggedMoves++; return; }
        _nextMoveLogAt = now + MoveLogIntervalMs;
        var unlogged = _unloggedMoves > 0 ? $", {_unloggedMoves} more since the last line" : "";
        if (_refreshes > 0) unlogged += $", {_refreshes} monitor re-read(s)";
        _unloggedMoves = 0;
        _refreshes = 0;
        Log.Info($"touch-cursor-guard moved the cursor {from.X},{from.Y} -> {to.X},{to.Y} ({reason}{unlogged})");
    }

    private static void FlushMoveLog()
    {
        if (_unloggedMoves > 0)
        {
            Log.Info($"touch-cursor-guard moved the cursor {_unloggedMoves} more times since the last line");
            _unloggedMoves = 0;
        }
        if (_refreshes > 0)
        {
            Log.Info($"touch-cursor-guard re-read the monitors {_refreshes} time(s) since the last line");
            _refreshes = 0;
        }
        FlushEntryLog();
    }

    // PanelKioskWindow swaps in a new array whenever a kiosk opens or closes, so the
    // converted copy is rebuilt only then, not on every mouse event.
    private static Native.RECT[]? _kioskSource;
    private static (int Left, int Top, int Right, int Bottom)[] _kioskTuples = Array.Empty<(int, int, int, int)>();

    private static (int Left, int Top, int Right, int Bottom)[] KioskRectTuples()
    {
        var kiosks = PanelKioskWindow.KioskRects;
        if (ReferenceEquals(kiosks, _kioskSource)) return _kioskTuples;
        var rects = new (int Left, int Top, int Right, int Bottom)[kiosks.Length];
        for (var i = 0; i < rects.Length; i++) rects[i] = (kiosks[i].Left, kiosks[i].Top, kiosks[i].Right, kiosks[i].Bottom);
        _kioskSource = kiosks;
        return _kioskTuples = rects;
    }

    private static long _nextEntryLogAt;
    private static int _entries;
    private static int _entryMaxSlidePx;

    private static void LogEntry(int slidePx)
    {
        _entries++;
        _entryMaxSlidePx = Math.Max(_entryMaxSlidePx, slidePx);
        if (Environment.TickCount64 >= _nextEntryLogAt) FlushEntryLog();
    }

    private static void FlushEntryLog()
    {
        if (_entries == 0) return;
        Log.Info($"touch-cursor-guard let the mouse slide onto a kiosk {_entries} time(s), largest slide {_entryMaxSlidePx} px");
        _entries = 0;
        _entryMaxSlidePx = 0;
        _nextEntryLogAt = Environment.TickCount64 + MoveLogIntervalMs;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode == Native.HC_ACTION && lParam != IntPtr.Zero && Native.GetCursorPos(out var c))
            {
                var hook = (Native.MSLLHOOKSTRUCT*)lParam;
                var isWmMouseMove = wParam == Native.WM_MOUSEMOVE;
                var result = Machine.Hook((hook->pt.x, hook->pt.y), (c.x, c.y), isWmMouseMove,
                    isWmMouseMove && !CursorGuardGeometry.IsTouchOrPen(hook->dwExtraInfo), Environment.TickCount64);
                if (result.Block) return 1;
                if (isWmMouseMove && !CursorGuardGeometry.IsTouchOrPen(hook->dwExtraInfo)) _realMouseMovePending = true;
                if (result.EntryAccepted) LogEntry(result.AcceptedSlidePx);
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
            if (_realMouseMovePending)
            {
                _realMouseMovePending = false;
                RealMouseMoved?.Invoke();
            }
            var result = Machine.CursorMoved((c.x, c.y), Environment.TickCount64);
            switch (result.Kind)
            {
                case GuardEventKind.EntryAccepted:
                    LogEntry(result.SlidePx);
                    break;
                case GuardEventKind.MoveCursor:
                    LogMove((c.x, c.y), result.Target, result.Reason);
                    Native.SetCursorPos(result.Target.X, result.Target.Y);
                    break;
                case GuardEventKind.HoldStranded:
                    Log.Info($"touch-cursor-guard no monitor to return to; holding {result.Target.X},{result.Target.Y}");
                    break;
            }
        }
        catch { /* an exception must never cross back into the native event callback */ }
    }
}
