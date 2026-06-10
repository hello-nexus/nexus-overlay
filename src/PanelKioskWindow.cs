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
/// dashboard + per-monitor widget overlays so the Chromium browser/GPU
/// process tree is shared across all three.
/// </summary>
internal sealed unsafe class PanelKioskWindow : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.PanelKiosk";
    private const string WindowTitle = "Nexus Panel";
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 3;
    private const int PermissionStateDeny = 2;
    // The panel SPA paints its own opaque background; this controls the
    // brief flash between WebView2 attach and first paint.
    private const uint DefaultBgArgbOpaqueBlack = 0xFF000000u;

    private static readonly ConcurrentDictionary<int, PanelKioskWindow> _instances = new();
    private static int _nextInstanceId;
    private readonly int _instanceId;

    public IntPtr Hwnd { get; private set; }
    public int MonitorIndex => _monitor.Index;

    private MonitorInfo _monitor;
    private readonly string _navigationUrl;
    private IntPtr _env;
    private IntPtr _envCreatedHandler;
    private IntPtr _ctrlCreatedHandler;
    private IntPtr _navStartingHandler;
    private IntPtr _newWindowHandler;
    private IntPtr _permissionHandler;
    private IntPtr _controller;
    private IntPtr _controller2;
    private IntPtr _coreWebView2;
    private long _navStartingToken;
    private long _newWindowToken;
    private long _permissionToken;
    private PanelMonitorGuard? _monitorGuard;
    private bool _disposed;

    public PanelKioskWindow(MonitorInfo monitor, string navigationUrl, bool guardMonitor)
    {
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _monitor = monitor;
        _navigationUrl = navigationUrl;

        try
        {
            var b = monitor.Bounds;
            // WS_EX_NOACTIVATE: touching the panel must not activate the kiosk.
            // Without it, touch on this secondary monitor activates the window and
            // Windows warps the system cursor to the contact point, stranding the
            // pointer on the Y70. Matches the legacy HYTE app (focusable:false) and
            // the sibling OverlayWindow.
            Hwnd = Win32Window.Create(
                WindowClassName,
                WindowTitle,
                Native.WS_POPUP,
                (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE),
                b.Left, b.Top, b.Width, b.Height,
                this);

            Log.Info($"panel-kiosk ctor monitor={monitor.Index} bounds={b.Left},{b.Top},{b.Width}x{b.Height} hwnd=0x{Hwnd:X} url={navigationUrl}");

            // Show the window before WebView2 attaches; the controller paints
            // over it once init finishes.
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
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
            // dispose it — a permanent black window.
            Dispose();
            throw;
        }
    }

    /// <summary>Monitor bounds this kiosk was created for (reconcile compares
    /// against fresh enumeration to catch arrangement/resolution changes).</summary>
    public Native.RECT MonitorBounds => _monitor.Bounds;

    /// <summary>
    /// Turn the foreign-window guard on or off on the live kiosk. Idempotent:
    /// starting when already running (or stopping when already off) is a no-op.
    /// Runs on the message-loop thread (ctor + prefs poll), same as the guard's
    /// hook install/teardown.
    /// </summary>
    public void SetMonitorGuard(bool enabled)
    {
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
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nexus", "DesktopWebView2");
        try { Directory.CreateDirectory(userDataDir); } catch { /* best-effort */ }

        _envCreatedHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);
        fixed (char* udf = userDataDir)
        {
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(
                null, udf, IntPtr.Zero, _envCreatedHandler);
            Log.Info($"panel-kiosk CreateCoreWebView2Env hr=0x{hr:X8}");
            if (WebView2Native.Failed(hr)) Log.Error($"panel-kiosk env init failed hr=0x{hr:X8}");
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
            Wv2.Ctrl2_put_DefaultBackgroundColor(_controller2, DefaultBgArgbOpaqueBlack);
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

        _newWindowHandler = WebView2Callbacks.CreateNewWindowRequestedHandler(&OnNewWindowRequestedStatic);
        Wv2.Wv2_add_NewWindowRequested(_coreWebView2, _newWindowHandler, out _newWindowToken);

        _permissionHandler = WebView2Callbacks.CreatePermissionRequestedHandler(&OnPermissionRequestedStatic);
        Wv2.Wv2_add_PermissionRequested(_coreWebView2, _permissionHandler, out _permissionToken);

        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"panel-kiosk Navigate hr=0x{hr:X8} url={_navigationUrl}");

        // Re-assert topmost AFTER WebView2 attach. Some controller-init
        // paths reorder windows; this guarantees we land on top.
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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
                Log.Info($"panel-kiosk external nav redirected: {uri}");
            }
        }
        catch (Exception ex) { Log.Error($"panel-kiosk NavStarting: {ex.Message}"); }
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
            Log.Info($"panel-kiosk new-window routed: {uri}");
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
    private static PanelKioskWindow? FindByNewWindowHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._newWindowHandler == h) return kv.Value; return null; }
    private static PanelKioskWindow? FindByPermissionHandler(IntPtr h)
    { foreach (var kv in _instances) if (kv.Value._permissionHandler == h) return kv.Value; return null; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.TryRemove(_instanceId, out _);
        _monitorGuard?.Dispose();
        _monitorGuard = null;
        try
        {
            if (_coreWebView2 != IntPtr.Zero)
            {
                if (_navStartingToken != 0) { Wv2.Wv2_remove_NavigationStarting(_coreWebView2, _navStartingToken); _navStartingToken = 0; }
                if (_newWindowToken != 0) { Wv2.Wv2_remove_NewWindowRequested(_coreWebView2, _newWindowToken); _newWindowToken = 0; }
                if (_permissionToken != 0) { Wv2.Wv2_remove_PermissionRequested(_coreWebView2, _permissionToken); _permissionToken = 0; }
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
            if (_envCreatedHandler != IntPtr.Zero) { Wv2.Release(_envCreatedHandler); _envCreatedHandler = IntPtr.Zero; }
            if (_ctrlCreatedHandler != IntPtr.Zero) { Wv2.Release(_ctrlCreatedHandler); _ctrlCreatedHandler = IntPtr.Zero; }
            if (_navStartingHandler != IntPtr.Zero) { Wv2.Release(_navStartingHandler); _navStartingHandler = IntPtr.Zero; }
            if (_newWindowHandler != IntPtr.Zero) { Wv2.Release(_newWindowHandler); _newWindowHandler = IntPtr.Zero; }
            if (_permissionHandler != IntPtr.Zero) { Wv2.Release(_permissionHandler); _permissionHandler = IntPtr.Zero; }
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
    }
}
