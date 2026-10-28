using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Fullscreen WebView2 window for the HYTE Y70/Y80 touch panel. Sized to
/// a single monitor's bounds, framed as a tool-window so it stays off the
/// taskbar, pinned topmost. Lives in the same overlay process as the
/// dashboard and the other kiosks so the Chromium browser/GPU process tree
/// is shared.
/// </summary>
internal sealed unsafe class PanelKioskWindow : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.PanelKiosk";
    // Separate class because the background brush is a class property: the
    // see-through kiosk must erase to nothing, so the desktop stays visible
    // wherever the page is transparent.
    private const string SeeThroughClassName = "Nexus.Overlay.PanelKioskClear";
    private const string WindowTitle = "Nexus Panel";
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 3;
    private const int PermissionStateDeny = 2;
    private static readonly UIntPtr TIMER_PAINT_POLL = new(11);
    private static readonly UIntPtr TIMER_NAV_RETRY = new(12);
    private static readonly UIntPtr TIMER_LEASE = new(13);
    private const uint LeaseCheckIntervalMs = 1_000;
    private const int WebErrorOperationCanceled = 14;
    // Watchdog policy shared by both kiosk owners (Program's Y70 branch and
    // MonitorKioskManager): deadline for a navigation to confirm content
    // before the owner recreates the window (the settings-toggle remedy,
    // automated). After RecreateFastAttempts unconfirmed recreates, the slow
    // deadline keeps a persistently broken WebView2 from churning spawns.
    internal const long ContentDeadlineMs = 20_000;
    internal const long ContentDeadlineSlowMs = 120_000;
    internal const int RecreateFastAttempts = 3;
    private const uint PaintPollIntervalMs = 2_000;
    // A first-paint performance entry exists only once the renderer's
    // compositor produced a frame. NavigationCompleted alone is not proof of
    // content: a kiosk created seconds after logon can report a successful
    // navigation while the renderer sits parked pre-first-paint (bench-hit
    // on the Y70 - 14MB renderer, no SPA execution; the kiosk shows only the
    // opaque default background). A parked renderer also never answers
    // ExecuteScript, so either way the confirmation stays unset and the
    // owner's watchdog recreates.
    private const string PaintProbeScript =
        "(function(){try{return performance.getEntriesByType('paint').length>0}catch(e){return false}})()";
    // COREWEBVIEW2_COLOR passed as a raw uint: the LOW byte is alpha (see
    // DashboardWindow.DefaultBgTransparent). Opaque black covers only the gap
    // between WebView2 attach and first paint; the SPA paints every visible
    // background itself (the desktop-wallpaper mode is an in-page image, so
    // the page is never transparent).
    private const uint DefaultBgOpaqueBlack = 0x000000FFu;
    // Alpha 0: WebView2 composites the page over whatever is behind the HWND,
    // so a page that paints no background shows the live desktop. Hit-testing
    // is by window region, not pixel alpha, so the panel still takes every
    // click and touch - do not pair this with WS_EX_TRANSPARENT.
    private const uint DefaultBgTransparent = 0x00000000u;

    private static readonly ConcurrentDictionary<int, PanelKioskWindow> _instances = new();
    // Monitor rects of the live kiosks, rebuilt when one is created, refit or
    // disposed. PointInAnyKiosk runs on every system mouse event, so it reads
    // this array instead of enumerating _instances.
    private static Native.RECT[] _kioskRects = Array.Empty<Native.RECT>();
    private static int _nextInstanceId;

    /// <summary>Snapshot of every open kiosk's monitor rect.</summary>
    internal static Native.RECT[] KioskRects => _kioskRects;
    private readonly int _instanceId;
    private readonly bool _seeThrough;
    private readonly bool _compatibilityRendering;
    private bool _isPrimary;
    private bool _clickThrough;
    private Native.RECT _placed;
    private FrostLayer? _frost;
    private readonly PanelActivation _activation = new();
    private bool _touchGuardHeld;
    private readonly PanelInputState _input;

    // Y70 compatibility rendering: some AMD drivers scramble a full-screen
    // DirectComposition window on a rotated display once focus or input
    // changes. Its own user-data folder, because browser arguments bind to
    // the browser process that the shared DesktopWebView2 folder already runs.
    internal const string CompatibilityBrowserArguments = "--disable-direct-composition";

    public IntPtr Hwnd { get; private set; }
    public int MonitorIndex => _monitor.Index;

    /// <summary>
    /// True once the renderer reports a first-paint performance entry (see
    /// PaintProbeScript). The owner's poll recreates a kiosk whose content
    /// never confirms.
    /// </summary>
    public bool HasConfirmedContent { get; private set; }

    /// <summary>The browser or renderer process died or hung; only a recreate recovers.</summary>
    public bool Failed { get; private set; }

    public bool SeeThrough => _seeThrough;

    public bool CompatibilityRendering => _compatibilityRendering;

    public bool Topmost => _input.Topmost;

    public bool IsPrimary => _isPrimary;

    /// <summary>
    /// Fires once when this kiosk's monitor became the primary display while the
    /// kiosk was open and its guards were dropped in place.
    /// </summary>
    public Action? PrimaryPromoted;

    /// <summary>
    /// This kiosk is showing nothing usable and only a recreate recovers it:
    /// its renderer died, or it never confirmed a first paint. The deadline
    /// stretches after repeated attempts so a broken WebView2 cannot churn.
    /// </summary>
    public bool Unhealthy(int attempts)
        => (Failed || !HasConfirmedContent)
           && AgeMs > (attempts >= RecreateFastAttempts ? ContentDeadlineSlowMs : ContentDeadlineMs);

    /// <summary>Milliseconds since construction; the content-watchdog deadline base.</summary>
    public long AgeMs => Environment.TickCount64 - _createdTick;

    /// <summary>
    /// Fires once, at the end of the one real Dispose (the _disposed guard
    /// blocks the WM_DESTROY reentrancy below from firing it twice). Lets
    /// the owner drop its reference when the HWND dies by a path other than
    /// its own explicit close call.
    /// </summary>
    public Action? Destroyed;

    private MonitorInfo _monitor;
    private readonly string _navigationUrl;
    private readonly bool _refitOnDisplayChange;
    // EDID-stable id of the display this kiosk hosts; the key WM_DISPLAYCHANGE
    // re-resolves the monitor by. Same value MonitorKioskManager keys on.
    private readonly string _stableDisplayId;
    private IntPtr _env;
    private IntPtr _envOptions;
    private IntPtr _envCreatedHandler;
    private IntPtr _ctrlCreatedHandler;
    private IntPtr _navStartingHandler;
    private IntPtr _navCompletedHandler;
    private IntPtr _execScriptHandler;
    private IntPtr _newWindowHandler;
    private IntPtr _permissionHandler;
    private IntPtr _processFailedHandler;
    private IntPtr _webMessageHandler;
    private IntPtr _controller;
    private IntPtr _controller2;
    private IntPtr _coreWebView2;
    private long _navStartingToken;
    private long _navCompletedToken;
    private long _newWindowToken;
    private long _permissionToken;
    private long _processFailedToken;
    private long _webMessageToken;
    private WebViewWidgetHost? _widgets;
    private int _navRetries;
    private readonly long _createdTick = Environment.TickCount64;
    private PanelMonitorGuard? _monitorGuard;
    private PanelTaskbarGuard? _taskbarGuard;
    private bool _disposed;

    /// <param name="refitOnDisplayChange">Refit this kiosk to its own display's
    /// fresh bounds directly from WM_DISPLAYCHANGE, ahead of the owner's
    /// reconcile. Off for the Y70 kiosk, whose owner drives bounds changes.</param>
    public PanelKioskWindow(MonitorInfo monitor, string navigationUrl, bool guardMonitor,
        bool refitOnDisplayChange = false, bool seeThrough = false, bool compatibilityRendering = false,
        bool topmost = true, bool clickThrough = false, bool isPrimary = false)
    {
        _seeThrough = seeThrough;
        _isPrimary = isPrimary;
        _clickThrough = seeThrough && clickThrough;
        _input = new PanelInputState(seeThrough, topmost);
        _compatibilityRendering = compatibilityRendering;
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _monitor = monitor;
        // WebView2 ticks at the host's display clock, not this panel's; the page divides it down.
        var refreshHz = Native.GetDisplayRefreshHz(monitor.DeviceName);
        _navigationUrl = refreshHz > 0 ? $"{navigationUrl}&displayHz={refreshHz}" : navigationUrl;
        _refitOnDisplayChange = refitOnDisplayChange;
        _stableDisplayId = DisplayIdentity.ResolveStableId(monitor.DeviceName);

        // Suppress the cursor-warp-on-touch for this monitor while the kiosk
        // is open; balanced in Dispose (also via the ctor's catch path).
        SnapshotKioskRects();
        // The primary display is the user's working desktop: the touch-cursor
        // guard exists for dedicated panel screens and must never act on it.
        if (!_isPrimary)
        {
            TouchCursorGuard.Acquire();
            _touchGuardHeld = true;
        }

        try
        {
            var b = TargetRect(monitor);
            _placed = b;
            // WS_EX_NOACTIVATE keeps a touch on this secondary monitor from
            // activating the kiosk and pulling focus off the foreground app.
            // The tap-warps-the-cursor symptom is a separate touch->mouse
            // promotion, suppressed by TouchCursorGuard.
            //
            // See-through kiosks skip the GDI redirection surface: hiding or
            // destroying a native child (the Web View widget) otherwise leaves
            // its old rect painted opaque black in that surface, which no
            // repaint or resize clears. WebView2 composes its own visuals, so it
            // needs no surface.
            //
            // Black class background: the region a Refit exposes erases to
            // black instead of to the desktop until the WebView2 covers it.
            // The class carries no CS_HREDRAW/CS_VREDRAW, so a resize erases
            // only that newly exposed region, never over live content.
            Hwnd = Win32Window.Create(
                seeThrough ? SeeThroughClassName : WindowClassName,
                WindowTitle,
                Native.WS_POPUP,
                (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | (topmost ? Native.WS_EX_TOPMOST : 0)
                    | (seeThrough ? Native.WS_EX_NOREDIRECTIONBITMAP : 0)),
                b.Left, b.Top, b.Width, b.Height,
                this,
                hbrBackground: seeThrough ? IntPtr.Zero : Native.GetStockObject(Native.BLACK_BRUSH));

            Log.Info($"panel-kiosk ctor monitor={monitor.Index} bounds={b.Left},{b.Top},{b.Width}x{b.Height} hwnd=0x{Hwnd:X} url={LogRedact.Url(navigationUrl)}");

            // A click-through kiosk starts with an empty region and only the
            // page's own hit-test report widens it, so a page that never loads
            // leaves the desktop usable.
            ApplyRegion();
            // Show the window before WebView2 attaches; the controller paints
            // over it once init finishes.
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
            // An opaque topmost kiosk covers the taskbar already; a see-through
            // or non-topmost one would show it. Never on the primary display.
            if (!_isPrimary && (seeThrough || !topmost)) _taskbarGuard = PanelTaskbarGuard.Start(Hwnd);
            if (!topmost) ApplyZOrder();
            StartWebView2Init();

            // When reserveMonitor is on, guard the panel monitor: relocate any
            // foreign window that comes to rest there. Toggled live via
            // SetMonitorGuard; torn down with the kiosk in Dispose.
            SetMonitorGuard(guardMonitor);
        }
        catch
        {
            // A throw mid-ctor (e.g. WebView2 loader missing) would otherwise
            // leak the already-shown fullscreen topmost HWND with no owner to
            // dispose it - a permanent black window.
            Dispose();
            throw;
        }
    }

    /// <summary>Monitor bounds this kiosk was created for (reconcile compares
    /// against fresh enumeration to catch arrangement/resolution changes).</summary>
    public Native.RECT MonitorBounds => _monitor.Bounds;

    /// <summary>The rect the window was last sized to: the work area on the primary display, else the monitor bounds.</summary>
    public Native.RECT PlacedBounds => _placed;

    /// <summary>The rect this kiosk should occupy on <paramref name="monitor"/> right now.</summary>
    public Native.RECT TargetRect(MonitorInfo monitor)
    {
        var t = KioskBounds.Target(_isPrimary, ToBox(monitor.Bounds), ToBox(monitor.WorkArea));
        return new Native.RECT { Left = t.Left, Top = t.Top, Right = t.Right, Bottom = t.Bottom };
    }

    /// <summary>Effective DPI of the monitor; 96 when it cannot be read.</summary>
    internal static uint MonitorDpi(MonitorInfo monitor)
    {
        try
        {
            var rect = monitor.Bounds;
            var handle = Native.MonitorFromRect(ref rect, Native.MONITOR_DEFAULTTONEAREST);
            if (handle != IntPtr.Zero && Native.GetDpiForMonitor(handle, Native.MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) return dx;
        }
        catch { /* shcore unavailable: 100% scale */ }
        return 96;
    }

    private static KioskBounds.Box ToBox(Native.RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>
    /// True when a screen point lies on any live kiosk's monitor. The gate
    /// <see cref="TouchCursorGuard"/> uses to scope cursor-warp suppression to
    /// touch on a panel screen. Reads <see cref="_instances"/> from the message-
    /// loop thread (where the low-level hook callback also runs).
    /// </summary>
    public static bool PointInAnyKiosk(int x, int y)
    {
        foreach (var b in _kioskRects)
        {
            if (x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom) return true;
        }
        return false;
    }

    /// <summary>
    /// Device names of the displays under every live kiosk on a non-primary
    /// display, whatever its guard, topmost or backdrop settings. Read from the
    /// window now: the kiosk's cached monitor can predate a renumbering.
    /// </summary>
    public static HashSet<string> PanelDevices()
    {
        var devices = new HashSet<string>();
        foreach (var kv in _instances)
        {
            var k = kv.Value;
            if (k._disposed || k._isPrimary || k.Hwnd == IntPtr.Zero) continue;
            var info = new Native.MonitorInfoNative { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfoNative>() };
            if (Native.GetMonitorInfoW(Native.MonitorFromWindow(k.Hwnd, Native.MONITOR_DEFAULTTONEAREST), ref info))
            {
                devices.Add(info.szDevice);
            }
        }
        return devices;
    }

    private static void SnapshotKioskRects()
    {
        var rects = new List<Native.RECT>();
        foreach (var kv in _instances)
        {
            if (!kv.Value._isPrimary && kv.Value._monitor is { } m) rects.Add(m.Bounds);
        }
        _kioskRects = rects.ToArray();
    }

    /// <summary>
    /// Re-hide a taskbar the shell restored on the see-through kiosk's
    /// monitor. Called from the owner's reconcile poll; a no-op for an opaque
    /// kiosk, which covers the taskbar anyway.
    /// </summary>
    public void ReassertTaskbar() => _taskbarGuard?.Reassert();

    /// <summary>
    /// Move the live kiosk into or out of the topmost band. Off re-sinks it to
    /// the bottom on every call, since windows the user moves onto the panel
    /// stack above it and the owner's poll keeps it there.
    /// </summary>
    public void SetTopmost(bool topmost)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        var changed = _input.SetTopmost(topmost);
        if (!changed && topmost) return;
        if (changed && !_seeThrough && !_isPrimary)
        {
            // Restore before going topmost: re-showing the taskbar raises it,
            // and nothing re-asserts a topmost kiosk afterwards.
            if (topmost)
            {
                _taskbarGuard?.Dispose();
                _taskbarGuard = null;
            }
            else
            {
                _taskbarGuard ??= PanelTaskbarGuard.Start(Hwnd);
            }
        }
        ApplyZOrder();
        if (changed) Log.Info($"panel-kiosk topmost={topmost} hwnd=0x{Hwnd:X}");
    }

    private void ApplyZOrder()
    {
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        if (_input.Topmost)
        {
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
            _frost?.Restack(Hwnd);
            return;
        }
        Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        // NOTOPMOST is a no-op on a window already in the normal band, so the
        // order is set explicitly: raised (a menu or sheet is open) goes to the
        // top of the normal band, never into the topmost one; otherwise it
        // sinks under the other windows.
        if (_input.SinkToBottom && _frost is not null && _frost.SinkWithKiosk(Hwnd)) return;
        Native.SetWindowPos(Hwnd, _input.SinkToBottom ? Native.HWND_BOTTOM : Native.HWND_TOP, 0, 0, 0, 0, flags);
        _frost?.Restack(Hwnd);
    }

    /// <summary>
    /// Live click-through toggle; no recreate. The page reports a different
    /// shape for each state, so the region drops to empty and it is asked again.
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        clickThrough = _seeThrough && clickThrough;
        if (clickThrough == _clickThrough) return;
        _clickThrough = clickThrough;
        if (!_seeThrough) return;
        OnPageLost(pageAlive: true, forceRequest: true);
    }

    private void ApplyRaise(bool raised)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        if (_input.SetRaised(raised)) ApplyZOrder();
    }

    // The host reset the region and raise state. When the page is gone
    // (navigation, renderer exit) its next load reports unprompted; when it is
    // still alive it never heard about the reset, so it is asked to report again.
    private void OnPageLost(bool pageAlive, bool forceRequest = false)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        var wasRaised = _input.Raised;
        var changed = _input.PageLost();
        if (changed) ApplyRegion();
        if (wasRaised) ApplyZOrder();
        Native.KillTimer(Hwnd, TIMER_LEASE);
        // A page that is gone can no longer clear it.
        if (!pageAlive) SetActivatable(false);
        // The page believes its plates exist; a live one is asked to report again.
        var hadFrost = _frost is { Count: > 0 };
        _frost?.Clear();
        if ((changed || forceRequest || hadFrost) && pageAlive) PostToPage(PanelHostMessage.RequestHitTestJson);
    }

    // WS_EX_NOACTIVATE keeps a click on the kiosk from stealing the foreground,
    // which also keeps keystrokes out of a text field, so the page drops it
    // while a text-editing surface is open.
    private void SetActivatable(bool value)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        if (!_activation.Set(value, Native.GetForegroundWindow(), Hwnd)) return;
        ApplyActivatable();
    }

    // The widget host reports text-field editing here so one place owns the style bit.
    private void SetWidgetEditing(bool value)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        if (!_activation.SetWidgetEditing(value, Native.GetForegroundWindow(), Hwnd)) return;
        ApplyActivatable();
    }

    private void ApplyActivatable()
    {
        var value = _activation.Activatable;
        var ex = Native.GetWindowLongW(Hwnd, Native.GWL_EXSTYLE);
        ex = value ? ex & ~Native.WS_EX_NOACTIVATE : ex | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtrW(Hwnd, Native.GWL_EXSTYLE, (IntPtr)ex);
        // NOREDRAW: a repaint of the regioned see-through window blinks the cards.
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE
            | Native.SWP_FRAMECHANGED | Native.SWP_NOREDRAW);
        Log.Info($"panel-kiosk activatable={value} hwnd=0x{Hwnd:X}");
        if (value) return;
        var prev = _activation.TakeRestoreTarget();
        if (Native.GetForegroundWindow() == Hwnd && prev != IntPtr.Zero
            && Native.IsWindow(prev) && Native.IsWindowVisible(prev) && !Native.IsIconic(prev))
        {
            Native.SetForegroundWindow(prev);
        }
    }

    // Frost plates exist only for a see-through panel on Windows 11 and only
    // while the page that asked for them is alive.
    private void ApplyFrost(FrostPlan plan)
    {
        if (_disposed || Hwnd == IntPtr.Zero || !_seeThrough || !FrostLayer.IsSupported) return;
        if (plan.IsEmpty)
        {
            _frost?.Clear();
            return;
        }
        _frost ??= new FrostLayer();
        _frost.Update(plan, Hwnd, _placed.Left, _placed.Top);
    }

    private void CheckLease()
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        if (_input.Expire(Environment.TickCount64, out var raiseLowered))
        {
            ApplyRegion();
            if (raiseLowered) ApplyZOrder();
            // The page's transient state outlived its renewals; ask it to re-send what still holds.
            PostToPage(PanelHostMessage.RequestHitTestJson);
            Log.Info($"panel-kiosk full-window lease lapsed hwnd=0x{Hwnd:X}");
        }
        if (!_input.LeaseActive) Native.KillTimer(Hwnd, TIMER_LEASE);
    }

    private void ApplyRegion()
    {
        if (Hwnd == IntPtr.Zero) return;
        var plan = _input.Effective;
        IntPtr region = IntPtr.Zero;
        switch (plan.Mode)
        {
            case HitMode.Full:
                break;
            case HitMode.Rects:
                region = Native.CreateRectRgn(0, 0, 0, 0);
                foreach (var r in plan.Rects)
                {
                    var part = Native.CreateRectRgn(r.X, r.Y, r.X + r.W, r.Y + r.H);
                    Native.CombineRgn(region, region, part, Native.RGN_OR);
                    Native.DeleteObject(part);
                }
                break;
            default:
                region = Native.CreateRectRgn(0, 0, 0, 0);
                break;
        }
        // The system owns the region after a successful call. bRedraw=false for
        // DWM re-clips to the region each frame.
        if (Native.SetWindowRgn(Hwnd, region, false) == 0 && region != IntPtr.Zero) Native.DeleteObject(region);
    }

    private void OnPanelMessage(PanelHostMessage msg)
    {
        if (_disposed) return;
        switch (msg.Kind)
        {
            case PanelMessageKind.HitTest:
                if (msg.Plan is { } plan)
                {
                    if (_input.ApplyReport(plan, Environment.TickCount64)) ApplyRegion();
                    // SetTimer with the same id replaces the previous timer, so each renewal re-arms the check.
                    if (_input.LeaseActive) Native.SetTimer(Hwnd, TIMER_LEASE, LeaseCheckIntervalMs, IntPtr.Zero);
                }
                break;
            case PanelMessageKind.Raise:
                ApplyRaise(msg.RaiseValue);
                break;
            case PanelMessageKind.Activatable:
                SetActivatable(msg.RaiseValue);
                break;
            case PanelMessageKind.Frost:
                if (msg.Frost is { } frost) ApplyFrost(frost);
                break;
        }
    }

    /// <summary>
    /// Turn the foreign-window guard on or off on the live kiosk. Idempotent:
    /// starting when already running (or stopping when already off) is a no-op.
    /// Runs on the message-loop thread (ctor + prefs poll), same as the guard's
    /// hook install/teardown.
    /// </summary>
    public void SetMonitorGuard(bool enabled)
    {
        if (_isPrimary) enabled = false;
        if (enabled)
        {
            _monitorGuard ??= PanelMonitorGuard.Start(Hwnd, _monitor);
        }
        else if (_monitorGuard is not null)
        {
            _monitorGuard.Dispose();
            _monitorGuard = null;
        }
    }

    /// <summary>
    /// Move the live kiosk onto <paramref name="monitor"/>'s current bounds
    /// (rotation or resolution change on the same display). The resulting
    /// WM_SIZE re-bounds the existing WebView2 controller, so the content
    /// resizes in place: no destroy, no re-navigate, no first paint to wait
    /// for. <see cref="HasConfirmedContent"/> stays set for that reason - the
    /// renderer that painted is the one being resized.
    /// </summary>
    public void Refit(MonitorInfo monitor)
    {
        if (_disposed || Hwnd == IntPtr.Zero) return;
        _monitor = monitor;
        SnapshotKioskRects();
        var b = TargetRect(monitor);
        _placed = b;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        // The guard resolved its HMONITOR and bounds from the window's old
        // rect at Start; both are stale after the move. Restart it against
        // the new rect (SetWindowPos above already placed the window there).
        if (_monitorGuard is not null)
        {
            _monitorGuard.Dispose();
            _monitorGuard = PanelMonitorGuard.Start(Hwnd, _monitor);
        }
        _taskbarGuard?.Retarget(Hwnd);
        _frost?.Reposition(b.Left, b.Top);
        // The page learns its new offset only from this message; the URL carried the initial one.
        PostToPage(KioskBounds.OriginMessageJson(ToBox(b), ToBox(monitor.Bounds), MonitorDpi(monitor)));
        Log.Info($"panel-kiosk refit monitor={monitor.Index} bounds={b.Left},{b.Top},{b.Width}x{b.Height} hwnd=0x{Hwnd:X}");
    }

    /// <summary>
    /// Re-resolve this kiosk's own display by its EDID-stable id and refit
    /// when its bounds moved. Resolved by identity rather than by
    /// MonitorFromWindow: mid-rotation the window still spans the pre-rotation
    /// rect, which can overlap a neighbouring monitor more than the panel it
    /// belongs to, and nearest-monitor would then move the kiosk onto that
    /// neighbour.
    /// </summary>
    private void RefitToOwnDisplay()
    {
        if (_disposed || Hwnd == IntPtr.Zero || string.IsNullOrEmpty(_stableDisplayId)) return;
        try
        {
            foreach (var m in Monitors.Enumerate())
            {
                if (!string.Equals(DisplayIdentity.ResolveStableId(m.DeviceName), _stableDisplayId, StringComparison.Ordinal))
                    continue;
                // Primary-ness comes from the host's own monitor info, so a promotion
                // takes effect even while the service is unreachable.
                if (m.Primary && !_isPrimary) PromoteToPrimary();
                if (SameBounds(TargetRect(m), _placed) && SameBounds(m.Bounds, _monitor.Bounds)) return;
                Refit(m);
                return;
            }
        }
        catch (Exception ex) { Log.Error($"panel-kiosk display-change refit: {ex.Message}"); }
    }

    private void PromoteToPrimary()
    {
        _isPrimary = true;
        SetMonitorGuard(false);
        _taskbarGuard?.Dispose();
        _taskbarGuard = null;
        if (_touchGuardHeld)
        {
            _touchGuardHeld = false;
            TouchCursorGuard.Release();
        }
        SnapshotKioskRects();
        Log.Info($"panel-kiosk monitor became primary; guards dropped hwnd=0x{Hwnd:X}");
        PrimaryPromoted?.Invoke();
    }

    private static bool SameBounds(Native.RECT a, Native.RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INIT_CONTROLLER:
                InitController();
                return IntPtr.Zero;

            case Native.WM_DISPLAYCHANGE:
                // Refit here rather than waiting for the owner's reconcile:
                // that path is gated behind two service HTTP round-trips, and
                // until it lands the window still spans its pre-rotation rect,
                // leaving the uncovered part of the panel showing the desktop.
                if (_refitOnDisplayChange) RefitToOwnDisplay();
                if (_touchGuardHeld) TouchCursorGuard.OnDisplayChange();
                return null;

            case Native.WM_SETTINGCHANGE:
                // Taskbar move, resize or auto-hide changes the work area the primary kiosk fills.
                if (_refitOnDisplayChange && _isPrimary && (long)wParam == Native.SPI_SETWORKAREA) RefitToOwnDisplay();
                return null;

            case Native.WM_SIZE:
                if (_controller != IntPtr.Zero)
                {
                    Native.GetClientRect(hwnd, out var rc);
                    Wv2.Ctrl_put_Bounds(_controller, rc);
                }
                _widgets?.OnKioskResized();
                return IntPtr.Zero;

            case Native.WM_MOVE:
                _widgets?.OnKioskMoved();
                return null;

            case Native.WM_SHOWWINDOW:
                if (wParam == IntPtr.Zero)
                {
                    _widgets?.DestroyAll("kiosk hidden");
                    _frost?.Clear();
                }
                return null;

            case Native.WM_ACTIVATE:
                if (((long)wParam & 0xFFFF) == 0)
                {
                    _widgets?.OnKioskDeactivated();
                    return null;
                }
                _activation.OnActivated(lParam, hwnd);
                // Activating a sunk kiosk raises it; the plates must follow.
                if (!_input.Topmost) _frost?.Restack(hwnd);
                // DefWindowProc would focus the kiosk itself, blurring the text field that asked for activation.
                if (_widgets?.FocusEditingView() == true) return IntPtr.Zero;
                if (!_activation.Activatable) return null;
                if (_controller != IntPtr.Zero && !Native.IsChild(hwnd, Native.GetFocus()))
                    Wv2.Ctrl_MoveFocusProgrammatic(_controller);
                return IntPtr.Zero;

            case WebViewWidgetHost.WM_WIDGET_DEFERRED:
                _widgets?.ProcessDeferred();
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
                if (lParam != IntPtr.Zero)
                {
                    var sug = *(Native.RECT*)lParam;
                    Native.SetWindowPos(hwnd, IntPtr.Zero,
                        sug.Left, sug.Top, sug.Width, sug.Height,
                        Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                    _placed = sug;
                    _frost?.Reposition(sug.Left, sug.Top);
                }
                return IntPtr.Zero;

            case Native.WM_TIMER:
                if (WebViewWidgetHost.OwnsTimer(wParam))
                {
                    _widgets?.OnTimer(wParam);
                    return IntPtr.Zero;
                }
                if (wParam == (IntPtr)(long)TIMER_PAINT_POLL.ToUInt64())
                {
                    ProbePaint();
                    return IntPtr.Zero;
                }
                if (wParam == (IntPtr)(long)TIMER_LEASE.ToUInt64())
                {
                    CheckLease();
                    return IntPtr.Zero;
                }
                if (wParam == (IntPtr)(long)TIMER_NAV_RETRY.ToUInt64())
                {
                    Native.KillTimer(hwnd, TIMER_NAV_RETRY);
                    if (!_disposed && _coreWebView2 != IntPtr.Zero) Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
                    return IntPtr.Zero;
                }
                return null;

            case Native.WM_DESTROY:
                Dispose();
                return IntPtr.Zero;
        }
        return null;
    }

    // ===================== WebView2 init flow =====================

    private void StartWebView2Init()
    {
        var userDataDir = UserDataPaths.Profile(
            _compatibilityRendering ? "PanelCompatWebView2" : "DesktopWebView2", ProfileMigration.UiState);
        try { Directory.CreateDirectory(userDataDir); } catch { /* best-effort */ }

        if (_compatibilityRendering) _envOptions = Wv2EnvironmentOptions.Create(CompatibilityBrowserArguments);
        _envCreatedHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);
        fixed (char* udf = userDataDir)
        {
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(
                null, udf, _envOptions, _envCreatedHandler);
            Log.Info($"panel-kiosk CreateCoreWebView2Env hr=0x{hr:X8} compat={_compatibilityRendering}");
            if (WebView2Native.Failed(hr))
            {
                Log.Error($"panel-kiosk env init failed hr=0x{hr:X8}");
                WebView2RuntimeInstaller.OnEnvInitFailed("panel-kiosk", hr, userInitiated: false);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnvCreatedStatic(IntPtr self, int errorCode, IntPtr env)
    {
        FindByEnvHandler(self)?.OnEnvCreated(errorCode, env);
        return WebView2Native.S_OK;
    }

    private void OnEnvCreated(int errorCode, IntPtr env)
    {
        Log.Info($"panel-kiosk env created err=0x{errorCode:X8} env=0x{env:X}");
        if (WebView2Native.Failed(errorCode) || env == IntPtr.Zero) return;
        Wv2.AddRef(env);
        _env = env;
        Native.PostMessageW(Hwnd, WM_INIT_CONTROLLER, IntPtr.Zero, IntPtr.Zero);
    }

    private void InitController()
    {
        if (_env == IntPtr.Zero) { Log.Error("panel-kiosk InitController: env null"); return; }
        _ctrlCreatedHandler = WebView2Callbacks.CreateControllerCreatedHandler(&OnControllerCreatedStatic);
        var hr = Wv2.Env_CreateCoreWebView2Controller(_env, Hwnd, _ctrlCreatedHandler);
        Log.Info($"panel-kiosk CreateCoreWebView2Controller hr=0x{hr:X8}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnControllerCreatedStatic(IntPtr self, int errorCode, IntPtr controller)
    {
        FindByCtrlHandler(self)?.OnControllerCreated(errorCode, controller);
        return WebView2Native.S_OK;
    }

    private void OnControllerCreated(int errorCode, IntPtr controller)
    {
        Log.Info($"panel-kiosk ctrl created err=0x{errorCode:X8} ctrl=0x{controller:X}");
        if (WebView2Native.Failed(errorCode) || controller == IntPtr.Zero)
        {
            Log.Error($"panel-kiosk controller creation failed hr=0x{errorCode:X8}");
            return;
        }
        Wv2.AddRef(controller);
        _controller = controller;

        _controller2 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller2);
        if (_controller2 != IntPtr.Zero)
        {
            Wv2.Ctrl2_put_DefaultBackgroundColor(_controller2,
                _seeThrough ? DefaultBgTransparent : DefaultBgOpaqueBlack);
        }

        Native.GetClientRect(Hwnd, out var rc);
        Wv2.Ctrl_put_Bounds(_controller, rc);
        Wv2.Ctrl_put_IsVisible(_controller, true);

        if (WebView2Native.Failed(Wv2.Ctrl_get_CoreWebView2(_controller, out _coreWebView2)) || _coreWebView2 == IntPtr.Zero)
        {
            Log.Error("panel-kiosk get_CoreWebView2 failed");
            return;
        }

        if (WebView2Native.Succeeded(Wv2.Wv2_get_Settings(_coreWebView2, out var settings)) && settings != IntPtr.Zero)
        {
            // The kiosk's whole surface is customer-facing interactive touch.
            // Strip context menus, status bar, zoom, and devtools (F12).
            Wv2.Settings_put_AreDefaultContextMenusEnabled(settings, false);
            Wv2.Settings_put_AreDevToolsEnabled(settings, false);
            Wv2.Settings_put_IsStatusBarEnabled(settings, false);
            Wv2.Settings_put_IsZoomControlEnabled(settings, false);
            Wv2.Release(settings);
        }

        // Same security posture as the dashboard window: external links go
        // to the default browser, popups go external, perm prompts deny.
        _navStartingHandler = WebView2Callbacks.CreateNavigationStartingHandler(&OnNavigationStartingStatic);
        Wv2.Wv2_add_NavigationStarting(_coreWebView2, _navStartingHandler, out _navStartingToken);

        _navCompletedHandler = WebView2Callbacks.CreateNavigationCompletedHandler(&OnNavigationCompletedStatic);
        Wv2.Wv2_add_NavigationCompleted(_coreWebView2, _navCompletedHandler, out _navCompletedToken);

        _newWindowHandler = WebView2Callbacks.CreateNewWindowRequestedHandler(&OnNewWindowRequestedStatic);
        Wv2.Wv2_add_NewWindowRequested(_coreWebView2, _newWindowHandler, out _newWindowToken);

        _permissionHandler = WebView2Callbacks.CreatePermissionRequestedHandler(&OnPermissionRequestedStatic);
        Wv2.Wv2_add_PermissionRequested(_coreWebView2, _permissionHandler, out _permissionToken);

        _processFailedHandler = WebView2Callbacks.CreateProcessFailedHandler(&OnProcessFailedStatic);
        Wv2.Wv2_add_ProcessFailed(_coreWebView2, _processFailedHandler, out _processFailedToken);

        _widgets = new WebViewWidgetHost(Hwnd, _navigationUrl, _compatibilityRendering, PostToPage, SetWidgetEditing);
        _webMessageHandler = WebView2Callbacks.CreateWebMessageReceivedHandler(&OnWebMessageReceivedStatic);
        Wv2.Wv2_add_WebMessageReceived(_coreWebView2, _webMessageHandler, out _webMessageToken);

        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"panel-kiosk Navigate hr=0x{hr:X8} url={LogRedact.Url(_navigationUrl)}");

        // Re-assert the z-order AFTER WebView2 attach: some controller-init
        // paths reorder windows.
        ApplyZOrder();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationStartingStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNavStartingHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.NavStartingArgs_get_Uri(args, out var uri);
            if (!IsLocalUri(uri))
            {
                Wv2.NavStartingArgs_put_Cancel(args, true);
                ShellOpenExternal(uri);
                Log.Info($"panel-kiosk external nav redirected: {LogRedact.Url(uri)}");
            }
            else
            {
                owner._widgets?.DestroyAll("kiosk navigation");
                owner.OnPageLost(pageAlive: false);
            }
        }
        catch (Exception ex) { Log.Error($"panel-kiosk NavStarting: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationCompletedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNavCompletedHandler(self);
        if (owner is null) return WebView2Native.S_OK;
        var ok = false;
        var status = 0;
        if (args != IntPtr.Zero)
        {
            Wv2.NavCompletedArgs_get_IsSuccess(args, out ok);
            Wv2.NavCompletedArgs_get_WebErrorStatus(args, out status);
        }
        Log.Info($"panel-kiosk navigation completed ok={ok} webErr={status}");
        if (!ok) owner.OnPageLost(pageAlive: false);
        if (ok)
        {
            owner._navRetries = 0;
            owner.StartPaintConfirmationPoll();
        }
        else if (status != WebErrorOperationCanceled && !owner._disposed && owner.Hwnd != IntPtr.Zero)
        {
            var delayMs = (uint)Math.Min(1000 << Math.Min(owner._navRetries++, 3), 8000);
            Native.SetTimer(owner.Hwnd, TIMER_NAV_RETRY, delayMs, IntPtr.Zero);
        }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnProcessFailedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByProcessFailedHandler(self);
        if (owner is null) return WebView2Native.S_OK;
        var kind = -1;
        if (args != IntPtr.Zero && WebView2Native.Succeeded(Wv2.ProcessFailedArgs_get_Kind(args, out var read)))
        {
            kind = read;
        }
        Log.Warn($"panel-kiosk process failed kind={kind}");
        owner._widgets?.DestroyAll("kiosk process failed");
        var pageGone = PanelActivation.IsFatalProcessFailure(kind);
        owner.OnPageLost(pageAlive: !pageGone);
        if (pageGone)
        {
            owner.Failed = true;
            Native.KillTimer(owner.Hwnd, TIMER_PAINT_POLL);
        }
        return WebView2Native.S_OK;
    }

    private void StartPaintConfirmationPoll()
    {
        if (_disposed || HasConfirmedContent || Hwnd == IntPtr.Zero) return;
        if (_execScriptHandler == IntPtr.Zero)
        {
            _execScriptHandler = WebView2Callbacks.CreateExecuteScriptCompletedHandler(&OnExecuteScriptCompletedStatic);
        }
        Native.SetTimer(Hwnd, TIMER_PAINT_POLL, PaintPollIntervalMs, IntPtr.Zero);
        ProbePaint();
    }

    private void ProbePaint()
    {
        if (_disposed || _coreWebView2 == IntPtr.Zero || _execScriptHandler == IntPtr.Zero) return;
        if (HasConfirmedContent)
        {
            Native.KillTimer(Hwnd, TIMER_PAINT_POLL);
            return;
        }
        Wv2.Wv2_ExecuteScript(_coreWebView2, PaintProbeScript, _execScriptHandler);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnExecuteScriptCompletedStatic(IntPtr self, int errorCode, IntPtr resultJsonPtr)
    {
        var owner = FindByExecScriptHandler(self);
        if (owner is null || owner._disposed) return WebView2Native.S_OK;
        if (WebView2Native.Failed(errorCode) || resultJsonPtr == IntPtr.Zero) return WebView2Native.S_OK;
        // resultJsonPtr is caller-owned (LPCWSTR in param); borrow, don't free.
        var json = Marshal.PtrToStringUni(resultJsonPtr);
        if (json == "true")
        {
            owner.HasConfirmedContent = true;
            Native.KillTimer(owner.Hwnd, TIMER_PAINT_POLL);
            Log.Info($"panel-kiosk first paint confirmed {owner.AgeMs} ms after create");
        }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNewWindowRequestedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNewWindowHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.NewWindowArgs_get_Uri(args, out var uri);
            Wv2.NewWindowArgs_put_Handled(args, true);
            ShellOpenExternal(uri);
            Log.Info($"panel-kiosk new-window routed: {LogRedact.Url(uri)}");
        }
        catch (Exception ex) { Log.Error($"panel-kiosk NewWindow: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnPermissionRequestedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByPermissionHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.PermissionArgs_get_PermissionKind(args, out var kind);
            Wv2.PermissionArgs_put_State(args, PermissionStateDeny);
            Log.Info($"panel-kiosk permission denied kind={kind}");
        }
        catch (Exception ex) { Log.Error($"panel-kiosk Permission: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnWebMessageReceivedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByWebMessageHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.WebMsgArgs_get_Source(args, out var source);
            var json = Wv2.WebMsgArgs_GetJson(args);
            // nexus.panel.* is the SPA talking to this host about its own window;
            // everything else belongs to the widget host.
            Native.GetClientRect(owner.Hwnd, out var rc);
            var panelMsg = PanelHostMessage.Parse(json, rc.Right - rc.Left, rc.Bottom - rc.Top);
            if (panelMsg.Kind == PanelMessageKind.NotPanel) owner._widgets?.OnWebMessage(source, json);
            else if (WebViewWidgetProtocol.IsPanelOrigin(source, owner._navigationUrl)) owner.OnPanelMessage(panelMsg);
        }
        catch (Exception ex) { Log.Error($"panel-kiosk WebMessage: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    private bool PostToPage(string json)
        => !_disposed && _coreWebView2 != IntPtr.Zero && WebView2Native.Succeeded(Wv2.Wv2_PostWebMessageAsJson(_coreWebView2, json));

    private static bool IsLocalUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("ws://localhost:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("ws://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShellOpenExternal(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        try { Native.ShellExecuteW(IntPtr.Zero, "open", uri, null, null, Native.SW_SHOW); }
        catch (Exception ex) { Log.Error($"panel-kiosk ShellExecute: {ex.Message}"); }
    }

    // ===================== Handler-to-instance lookup =====================

    private static PanelKioskWindow? FindByEnvHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._envCreatedHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByCtrlHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._ctrlCreatedHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByNavStartingHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._navStartingHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByNavCompletedHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._navCompletedHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByExecScriptHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._execScriptHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByNewWindowHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._newWindowHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByProcessFailedHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._processFailedHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByWebMessageHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._webMessageHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByPermissionHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._permissionHandler == h) return kv.Value; return null; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.TryRemove(_instanceId, out _);
        SnapshotKioskRects();
        if (_touchGuardHeld)
        {
            _touchGuardHeld = false;
            TouchCursorGuard.Release();
        }
        _taskbarGuard?.Dispose();
        _taskbarGuard = null;
        _monitorGuard?.Dispose();
        _monitorGuard = null;
        _frost?.Dispose();
        _frost = null;
        try
        {
            _widgets?.Dispose();
            _widgets = null;
            if (_coreWebView2 != IntPtr.Zero)
            {
                if (_webMessageToken != 0) { Wv2.Wv2_remove_WebMessageReceived(_coreWebView2, _webMessageToken); _webMessageToken = 0; }
                if (_navStartingToken != 0) { Wv2.Wv2_remove_NavigationStarting(_coreWebView2, _navStartingToken); _navStartingToken = 0; }
                if (_navCompletedToken != 0) { Wv2.Wv2_remove_NavigationCompleted(_coreWebView2, _navCompletedToken); _navCompletedToken = 0; }
                if (_newWindowToken != 0) { Wv2.Wv2_remove_NewWindowRequested(_coreWebView2, _newWindowToken); _newWindowToken = 0; }
                if (_permissionToken != 0) { Wv2.Wv2_remove_PermissionRequested(_coreWebView2, _permissionToken); _permissionToken = 0; }
                if (_processFailedToken != 0) { Wv2.Wv2_remove_ProcessFailed(_coreWebView2, _processFailedToken); _processFailedToken = 0; }
            }
            if (_controller != IntPtr.Zero)
            {
                Wv2.Ctrl_Close(_controller);
                Wv2.Release(_controller);
                _controller = IntPtr.Zero;
            }
            if (_coreWebView2 != IntPtr.Zero) { Wv2.Release(_coreWebView2); _coreWebView2 = IntPtr.Zero; }
            if (_controller2 != IntPtr.Zero) { Wv2.Release(_controller2); _controller2 = IntPtr.Zero; }
            if (_env != IntPtr.Zero) { Wv2.Release(_env); _env = IntPtr.Zero; }
            if (_envOptions != IntPtr.Zero) { Wv2.Release(_envOptions); _envOptions = IntPtr.Zero; }
            if (_envCreatedHandler != IntPtr.Zero) { Wv2.Release(_envCreatedHandler); _envCreatedHandler = IntPtr.Zero; }
            if (_ctrlCreatedHandler != IntPtr.Zero) { Wv2.Release(_ctrlCreatedHandler); _ctrlCreatedHandler = IntPtr.Zero; }
            if (_navStartingHandler != IntPtr.Zero) { Wv2.Release(_navStartingHandler); _navStartingHandler = IntPtr.Zero; }
            if (_navCompletedHandler != IntPtr.Zero) { Wv2.Release(_navCompletedHandler); _navCompletedHandler = IntPtr.Zero; }
            if (_execScriptHandler != IntPtr.Zero) { Wv2.Release(_execScriptHandler); _execScriptHandler = IntPtr.Zero; }
            if (_newWindowHandler != IntPtr.Zero) { Wv2.Release(_newWindowHandler); _newWindowHandler = IntPtr.Zero; }
            if (_permissionHandler != IntPtr.Zero) { Wv2.Release(_permissionHandler); _permissionHandler = IntPtr.Zero; }
            if (_processFailedHandler != IntPtr.Zero) { Wv2.Release(_processFailedHandler); _processFailedHandler = IntPtr.Zero; }
            if (_webMessageHandler != IntPtr.Zero) { Wv2.Release(_webMessageHandler); _webMessageHandler = IntPtr.Zero; }
        }
        catch (Exception ex) { Log.Error($"panel-kiosk dispose: {ex.Message}"); }
        if (Hwnd != IntPtr.Zero)
        {
            // Destroy the actual HWND. Without this, the fullscreen
            // WS_EX_TOPMOST opaque-black window stays painted on screen
            // until process exit even after the WebView2 controller has
            // released. WM_DESTROY re-enters this method but the
            // _disposed guard above short-circuits the second call.
            Native.DestroyWindow(Hwnd);
            Win32Window.Unregister(Hwnd);
            Hwnd = IntPtr.Zero;
        }
        Destroyed?.Invoke();
    }
}
