using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// One overlay per HMONITOR. A Win32 window hosts a WebView2 controller
/// (created via direct P/Invoke + manual vtables - no Microsoft.Web.WebView2.Core
/// dependency so we stay AOT-clean). The WebView2 swapchain composites
/// transparent pixels over the desktop; SetWindowRgn carves the window
/// down to the SPA-reported widget rects so areas outside widgets are
/// not part of the window (input falls through, no pixels painted).
/// </summary>
internal sealed unsafe class OverlayWindow : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.HostWindow";
    private const int CornerEllipsePx = 20;
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 1;

    // Per-instance dispatch back from static [UnmanagedCallersOnly] callbacks.
    // Each overlay's WebView2 callback handler pointers identify it in the
    // _instances map. We use a process-wide registry because
    // [UnmanagedCallersOnly] methods cannot capture managed state.
    private static readonly ConcurrentDictionary<int, OverlayWindow> _instances = new();
    private static int _nextInstanceId;
    private readonly int _instanceId;

    public IntPtr Hwnd { get; private set; }
    public int MonitorIndex => _monitor.Index;

    private MonitorInfo _monitor;
    private readonly string _navigationUrl;
    private bool _alwaysOnTop;
    private bool _zOrderApplied;

    private IntPtr _env;
    private IntPtr _envCreatedHandler;
    private IntPtr _ctrlCreatedHandler;
    private IntPtr _webMsgHandler;
    private IntPtr _navCompletedHandler;
    private IntPtr _controller;          // ICoreWebView2Controller
    private IntPtr _controller2;         // ICoreWebView2Controller2 (for DefaultBackgroundColor)
    private IntPtr _coreWebView2;        // ICoreWebView2
    private long _webMsgToken;
    private long _navCompletedToken;
    private bool _disposed;

    public OverlayWindow(MonitorInfo monitor, string navigationUrl, bool alwaysOnTop)
    {
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _monitor = monitor;
        _navigationUrl = navigationUrl;
        _alwaysOnTop = alwaysOnTop;

        var b = monitor.Bounds;
        Hwnd = Win32Window.Create(
            WindowClassName,
            $"Nexus Overlay ({monitor.Index})",
            Native.WS_POPUP,
            (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            b.Left, b.Top, b.Width, b.Height,
            this);

        // Hide everything until the SPA reports widget rects: 1x1 region.
        var emptyRgn = Native.CreateRectRgn(0, 0, 1, 1);
        Native.SetWindowRgn(Hwnd, emptyRgn, false);

        Log.Info($"overlay ctor monitor={monitor.Index} bounds={b.Left},{b.Top},{b.Width}x{b.Height} hwnd=0x{Hwnd:X} url={LogRedact.Url(navigationUrl)}");

        Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);

        // Kick off WebView2 init. The env-created callback fires
        // asynchronously on the sync-context-installed UI thread.
        StartWebView2Init();
    }

    public void SetAlwaysOnTop(bool value)
    {
        if (_alwaysOnTop == value && _zOrderApplied) return;
        _alwaysOnTop = value;
        ApplyZOrder();
    }

    /// <summary>
    /// Move the existing overlay window to the bounds of a different
    /// monitor without recreating it. Reuses the same WebView2 controller
    /// and child process tree - the only visible effect is the window
    /// jumping to the new screen. WM_SIZE fires from SetWindowPos and the
    /// existing handler resizes the controller to match.
    ///
    /// Out-of-range index falls back to the primary monitor (mirrors
    /// Program.CreateOverlay's resolution).
    /// </summary>
    public bool MoveToMonitor(int requestedIndex)
    {
        if (Hwnd == IntPtr.Zero) return false;
        var monitors = Monitors.Enumerate();
        if (monitors.Count == 0) return false;

        MonitorInfo target;
        if (requestedIndex >= 0 && requestedIndex < monitors.Count)
        {
            target = monitors[requestedIndex];
        }
        else
        {
            MonitorInfo? primary = null;
            foreach (var m in monitors) if (m.Primary) { primary = m; break; }
            target = primary ?? monitors[0];
        }

        var b = target.Bounds;
        // SWP_NOACTIVATE so we don't steal focus.
        // SWP_NOZORDER because hWndInsertAfter=0 would otherwise raise us
        // to the top of the non-topmost band; ApplyZOrder below re-sinks
        // to HWND_BOTTOM but the flash is visible without this flag.
        // No same-index short-circuit: a resolution / arrangement change
        // can produce different bounds for the same index, so we always
        // re-apply.
        Native.SetWindowPos(Hwnd, IntPtr.Zero,
            b.Left, b.Top, b.Width, b.Height,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        Log.Info($"overlay MoveToMonitor: {_monitor.Index} -> {target.Index} bounds={b.Left},{b.Top},{b.Width}x{b.Height}");
        _monitor = target;
        // WM_SIZE fires from SetWindowPos and our handler resizes the
        // controller; the SPA's reportLayout webMessage repaints the
        // SetWindowRgn carve-out for the new client area.
        ApplyZOrder();
        return true;
    }

    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INIT_CONTROLLER:
                InitController();
                return IntPtr.Zero;
            case Native.WM_SIZE:
                if (_controller != IntPtr.Zero)
                {
                    Native.GetClientRect(hwnd, out var rc);
                    Wv2.Ctrl_put_Bounds(_controller, rc);
                }
                return IntPtr.Zero;
            case Native.WM_DPICHANGED:
                // lParam points to a RECT* with the OS-suggested new bounds
                // at the new DPI. Honoring it keeps the overlay sized when
                // dragged between monitors of different scale, or on a display
                // swap. WM_SIZE fires from the SetWindowPos and reflows the
                // controller; the SPA's reportLayout re-emits and refreshes
                // the SetWindowRgn carve-out for the new client area.
                if (lParam != IntPtr.Zero)
                {
                    var sug = *(Native.RECT*)lParam;
                    Native.SetWindowPos(hwnd, IntPtr.Zero,
                        sug.Left, sug.Top, sug.Width, sug.Height,
                        Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                }
                return IntPtr.Zero;
            case Native.WM_DESTROY:
                Dispose();
                return IntPtr.Zero;
        }
        return null;
    }

    // ===================== WebView2 init flow =====================

    private void StartWebView2Init()
    {
        // %ProgramData%\Nexus\DesktopWebView2 - shared between SYSTEM and user
        // sessions.
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nexus", "DesktopWebView2");
        try { Directory.CreateDirectory(userDataDir); } catch { /* best-effort */ }

        _envCreatedHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);

        fixed (char* udf = userDataDir)
        {
            // Null env options: skips renderer-process-limit consolidation.
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(
                null, udf, IntPtr.Zero, _envCreatedHandler);
            Log.Info($"overlay {_monitor.Index} CreateCoreWebView2Env hr=0x{hr:X8}");
            if (WebView2Native.Failed(hr))
            {
                Log.Error($"overlay {_monitor.Index} env init failed hr=0x{hr:X8}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnvCreatedStatic(IntPtr self, int errorCode, IntPtr env)
    {
        var owner = FindByEnvHandler(self);
        owner?.OnEnvCreated(errorCode, env);
        return WebView2Native.S_OK;
    }

    private void OnEnvCreated(int errorCode, IntPtr env)
    {
        Log.Info($"overlay {_monitor.Index} env created err=0x{errorCode:X8} env=0x{env:X}");
        if (WebView2Native.Failed(errorCode) || env == IntPtr.Zero) return;
        Wv2.AddRef(env);
        _env = env;
        // Defer controller creation out of WebView2's own callback frame.
        Native.PostMessageW(Hwnd, WM_INIT_CONTROLLER, IntPtr.Zero, IntPtr.Zero);
    }

    private void InitController()
    {
        if (_env == IntPtr.Zero) { Log.Error($"overlay {_monitor.Index} InitController: env null"); return; }
        _ctrlCreatedHandler = WebView2Callbacks.CreateControllerCreatedHandler(&OnControllerCreatedStatic);
        var hr = Wv2.Env_CreateCoreWebView2Controller(_env, Hwnd, _ctrlCreatedHandler);
        Log.Info($"overlay {_monitor.Index} CreateCoreWebView2Controller hr=0x{hr:X8}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnControllerCreatedStatic(IntPtr self, int errorCode, IntPtr controller)
    {
        var owner = FindByCtrlHandler(self);
        owner?.OnControllerCreated(errorCode, controller);
        return WebView2Native.S_OK;
    }

    private void OnControllerCreated(int errorCode, IntPtr controller)
    {
        Log.Info($"overlay {_monitor.Index} ctrl created err=0x{errorCode:X8} ctrl=0x{controller:X}");
        if (WebView2Native.Failed(errorCode) || controller == IntPtr.Zero)
        {
            Log.Error($"overlay {_monitor.Index} controller creation failed hr=0x{errorCode:X8}");
            return;
        }
        Wv2.AddRef(controller);
        _controller = controller;

        // QI for ICoreWebView2Controller2 - lets us set the transparent
        // default background color.
        _controller2 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller2);
        if (_controller2 != IntPtr.Zero)
        {
            // 0x00000000 = transparent ARGB; widget cards paint their own
            // opaque surface via CSS.
            Wv2.Ctrl2_put_DefaultBackgroundColor(_controller2, 0u);
        }
        else
        {
            Log.Warn($"overlay {_monitor.Index} no ICoreWebView2Controller2; transparent bg unavailable");
        }

        // Pin the WebView2 to 1 device-independent px = 1 raw px and stop it
        // tracking monitor DPI. Desktop widget size must come only from the
        // in-app OverlayWidgetScale pref (the SPA's cellPx), never from Windows
        // display scaling: otherwise a non-100% monitor rasterizes the widgets
        // larger while the SetWindowRgn carve-out - built from the SPA's CSS-px
        // rects - stays unscaled, so the mask no longer covers the widget. With
        // the scale pinned at 1.0, devicePixelRatio is 1, CSS px == raw px, and
        // the carve-out lines up at any display scaling.
        var controller3 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller3);
        if (controller3 != IntPtr.Zero)
        {
            Wv2.Ctrl3_put_ShouldDetectMonitorScaleChanges(controller3, false);
            Wv2.Ctrl3_put_RasterizationScale(controller3, 1.0);
            Wv2.Release(controller3);
        }
        else
        {
            Log.Warn($"overlay {_monitor.Index} no ICoreWebView2Controller3; DPI scale pin unavailable");
        }

        // Bounds + visibility.
        Native.GetClientRect(Hwnd, out var rc);
        Wv2.Ctrl_put_Bounds(_controller, rc);
        Wv2.Ctrl_put_IsVisible(_controller, true);

        // Inner ICoreWebView2. The getter AddRef's the out-param per COM
        // contract, so we don't AddRef again here - doing so would leak
        // one reference on every overlay teardown / respawn cycle.
        if (WebView2Native.Failed(Wv2.Ctrl_get_CoreWebView2(_controller, out _coreWebView2)) || _coreWebView2 == IntPtr.Zero)
        {
            Log.Error($"overlay {_monitor.Index} get_CoreWebView2 failed");
            return;
        }

        // Apply settings.
        if (WebView2Native.Succeeded(Wv2.Wv2_get_Settings(_coreWebView2, out var settings)) && settings != IntPtr.Zero)
        {
            Wv2.Settings_put_AreDefaultContextMenusEnabled(settings, false);
            Wv2.Settings_put_AreDevToolsEnabled(settings, true);
            Wv2.Settings_put_IsStatusBarEnabled(settings, false);
            Wv2.Settings_put_IsZoomControlEnabled(settings, false);
            Wv2.Release(settings);
        }

        // Hook events.
        _webMsgHandler = WebView2Callbacks.CreateWebMessageReceivedHandler(&OnWebMessageStatic);
        Wv2.Wv2_add_WebMessageReceived(_coreWebView2, _webMsgHandler, out _webMsgToken);

        _navCompletedHandler = WebView2Callbacks.CreateNavigationCompletedHandler(&OnNavigationCompletedStatic);
        Wv2.Wv2_add_NavigationCompleted(_coreWebView2, _navCompletedHandler, out _navCompletedToken);

        // Navigate.
        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"overlay {_monitor.Index} Navigate hr=0x{hr:X8} url={LogRedact.Url(_navigationUrl)}");

        ApplyZOrder();

    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationCompletedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNavHandler(self);
        if (owner is null) return WebView2Native.S_OK;
        Wv2.NavCompletedArgs_get_IsSuccess(args, out var ok);
        Wv2.NavCompletedArgs_get_WebErrorStatus(args, out var status);
        Log.Info($"overlay {owner._monitor.Index} navigation completed ok={ok} webErr={status}");
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnWebMessageStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByWebMsgHandler(self);
        if (owner is null) return WebView2Native.S_OK;
        try
        {
            if (WebView2Native.Failed(Wv2.WebMsgArgs_get_WebMessageAsJson(args, out var jsonPtr)) || jsonPtr == IntPtr.Zero)
                return WebView2Native.S_OK;
            var json = Marshal.PtrToStringUni(jsonPtr) ?? "";
            Marshal.FreeCoTaskMem(jsonPtr);
            owner.HandleWebMessage(json);
        }
        catch (Exception ex)
        {
            Log.Error($"OnWebMessage threw: {ex.Message}");
        }
        return WebView2Native.S_OK;
    }

    private void HandleWebMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            if (type == "reportLayout")
            {
                ApplyHitRegion(root);
            }
            else if (type == "setAlwaysOnTop"
                && root.TryGetProperty("value", out var valueEl)
                && (valueEl.ValueKind == JsonValueKind.True || valueEl.ValueKind == JsonValueKind.False))
            {
                var v = valueEl.GetBoolean();
                Log.Info($"webMsg setAlwaysOnTop={v}");
                Program.SetAllAlwaysOnTop(v);
            }
            else if (type == "setMonitor"
                && root.TryGetProperty("value", out var monEl)
                && monEl.ValueKind == JsonValueKind.Number
                && monEl.TryGetInt32(out var monIdx))
            {
                // SPA pushes this whenever the user picks a monitor in
                // the popup. Bypasses the 5 s prefs poll: window moves
                // instantly via SetWindowPos.
                Program.MoveAllToMonitor(monIdx);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"webMessage parse error: {ex.Message}");
        }
    }

    // ===================== Handler-to-instance lookup =====================

    private static OverlayWindow? FindByEnvHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._envCreatedHandler == handler) return kv.Value;
        return null;
    }

    private static OverlayWindow? FindByCtrlHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._ctrlCreatedHandler == handler) return kv.Value;
        return null;
    }

    private static OverlayWindow? FindByWebMsgHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._webMsgHandler == handler) return kv.Value;
        return null;
    }

    private static OverlayWindow? FindByNavHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._navCompletedHandler == handler) return kv.Value;
        return null;
    }

    // ===================== Hit region carve-out =====================

    private void ApplyHitRegion(JsonElement root)
    {
        var rects = RegionLayout.CollectRects(root);
        IntPtr region;
        if (rects.Count > 0)
        {
            var combined = Native.CreateRectRgn(0, 0, 0, 0);
            foreach (var r in rects)
            {
                var part = Native.CreateRoundRectRgn(r.X, r.Y, r.X + r.W, r.Y + r.H, CornerEllipsePx, CornerEllipsePx);
                Native.CombineRgn(combined, combined, part, Native.RGN_OR);
                Native.DeleteObject(part);
            }
            region = combined;
        }
        else
        {
            region = Native.CreateRectRgn(0, 0, 1, 1);
        }
        // Rects are raw device pixels: the overlay's WebView2 is pinned to
        // RasterizationScale 1.0 (see OnControllerCreated), so the SPA's CSS-px
        // layout maps 1:1 to device px and the carve-out matches the widgets at
        // any display scaling.
        // bRedraw=false: the region is a DWM clip re-applied every compositor
        // frame, so the new shape shows without an explicit redraw. bRedraw=true
        // forces a full-window repaint of the whole carved region on every
        // reportLayout, which blinks all widget cards at once (most visible when
        // opening the context menu adds its rect). Matches the ctor's empty-region
        // set, which already passes false.
        Native.SetWindowRgn(Hwnd, region, false);
        Log.Info($"overlay {_monitor.Index} region rebuilt rects={rects.Count}");
    }

    // ===================== Z-order =====================

    private void ApplyZOrder()
    {
        if (Hwnd == IntPtr.Zero) return;
        // Re-parent to the desktop only if something actually reparented us
        // (defends against a WorkerW capture). An unconditional SetParent on
        // every z-order apply repaints the window, contributing to a one-frame
        // flash on each menu-open / drag toggle.
        if (Native.GetParent(Hwnd) != IntPtr.Zero)
            Native.SetParent(Hwnd, IntPtr.Zero);

        // SWP_NOREDRAW: this is a regioned, DWM-composited window; the z-order
        // change still takes effect (the compositor re-stacks next frame), but
        // suppressing the GDI repaint avoids the full-window flash that fires on
        // every topmost<->bottom toggle (menu open/close, drag start/end).
        const uint zFlags = Native.SWP_NOMOVE | Native.SWP_NOSIZE
            | Native.SWP_NOACTIVATE | Native.SWP_NOREDRAW;
        if (_alwaysOnTop)
        {
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, zFlags);
            Log.Info($"overlay {_monitor.Index} zorder=topmost");
        }
        else
        {
            Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, zFlags);
            Native.SetWindowPos(Hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, zFlags);
            Log.Info($"overlay {_monitor.Index} zorder=bottom");
        }
        _zOrderApplied = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.TryRemove(_instanceId, out _);
        try
        {
            // Close + release the controller first - this tears the
            // WebView2 process tree down and invalidates the inner
            // ICoreWebView2 + Controller2 we hold. Release THOSE after
            // Close to maintain top-down ordering and avoid touching
            // pointers the close path may have already invalidated.
            if (_controller != IntPtr.Zero)
            {
                Wv2.Ctrl_Close(_controller);
                Wv2.Release(_controller);
                _controller = IntPtr.Zero;
            }
            if (_coreWebView2 != IntPtr.Zero) { Wv2.Release(_coreWebView2); _coreWebView2 = IntPtr.Zero; }
            if (_controller2 != IntPtr.Zero) { Wv2.Release(_controller2); _controller2 = IntPtr.Zero; }
            if (_env != IntPtr.Zero) { Wv2.Release(_env); _env = IntPtr.Zero; }
        }
        catch (Exception ex) { Log.Error($"overlay dispose: {ex.Message}"); }
        if (Hwnd != IntPtr.Zero)
        {
            // Destroy the actual HWND. Without this, the carved transparent
            // window stays painted on screen until process exit even after
            // the WebView2 controller has released - so a teardown (toggle
            // off, monitor switch) leaves a stale overlay whenever something
            // else keeps the process alive (open dashboard, kiosk). WM_DESTROY
            // re-enters this method but the _disposed guard short-circuits it.
            Native.DestroyWindow(Hwnd);
            Win32Window.Unregister(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }
}
