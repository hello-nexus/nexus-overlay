using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Qos.Overlay.WebView2;
using Qos.Overlay.Win32;

namespace Qos.Overlay;

/// <summary>
/// Standard top-level WebView2 window hosting the main Qos dashboard URL.
/// Replaces the Edge --app spawn the tray used to invoke. Lives in the
/// same process as the floating overlay widgets so the dashboard's
/// renderer shares the browser / GPU / network / utility process tree
/// already running for the overlays - this is where the memory win over
/// msedge --app comes from. Singleton per process (the overlay process
/// is already singleton via the mutex in Program.cs).
///
/// Closing via the X button hides the window rather than destroying it
/// so the next "Open Qos" click is instantaneous - no WebView2 cold
/// start. The hidden window is torn down only when the overlay process
/// itself exits.
/// </summary>
internal sealed unsafe class DashboardWindow : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Qos.Overlay.Dashboard";
    private const string WindowTitle = "Qos";
    private const int DefaultClientWidth = 1280;
    private const int DefaultClientHeight = 800;
    private const int MinClientWidth = 640;
    private const int MinClientHeight = 480;
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 2;
    private const int PermissionStateDeny = 2;
    // System "window" background brush handle. CSS-defined HBRUSH values
    // are (COLOR_INDEX + 1); COLOR_WINDOW is index 5.
    private const int ClassBgBrushColorWindow = (int)Native.COLOR_WINDOW + 1;
    // 32-bit ARGB the WebView2 controller paints behind the page until
    // the SPA's CSS background takes over. Opaque white avoids the
    // transparent flash that the overlay's compositing uses.
    private const uint DefaultBgArgbOpaqueWhite = 0xFFFFFFFFu;

    private static readonly ConcurrentDictionary<int, DashboardWindow> _instances = new();
    private static int _nextInstanceId;
    private readonly int _instanceId;

    public IntPtr Hwnd { get; private set; }

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
    private bool _disposed;
    private bool _saveOnClose;

    public DashboardWindow(string navigationUrl)
    {
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _navigationUrl = navigationUrl;

        var (x, y, w, h) = ResolveInitialBounds();

        // Pre-paint the client area with the system window color before
        // the WebView2 controller attaches; without this the first frame
        // shows whatever the desktop happens to composite under it.
        var bgBrush = (IntPtr)ClassBgBrushColorWindow;

        Hwnd = Win32Window.Create(
            WindowClassName,
            WindowTitle,
            Native.WS_OVERLAPPEDWINDOW | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0u,
            x, y, w, h,
            this,
            bgBrush);

        // Apply the system theme to the non-client (caption + frame) area
        // so the title bar reads dark when the user is on a dark theme,
        // matching first-party Win11 apps. The SPA handles its own dark
        // mode via prefers-color-scheme.
        ApplyImmersiveTheme(Hwnd, IsSystemDarkMode());

        Log.Info($"dashboard ctor bounds={x},{y},{w}x{h} hwnd=0x{Hwnd:X} url={navigationUrl}");
        StartWebView2Init();
    }

    private static void ApplyImmersiveTheme(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        // Try the modern attribute id first; fall back to the legacy id
        // on older builds where it had a different ordinal.
        var hr = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, value, sizeof(int));
        if (hr != 0)
        {
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, value, sizeof(int));
        }
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* registry is best-effort; default to light */ }
        return false;
    }

    public void ShowAndFocus()
    {
        if (Hwnd == IntPtr.Zero) return;
        // Re-read theme: covers the case where the user toggled light/dark
        // between dashboard sessions. The hidden window kept its previous
        // attribute value, which would otherwise paint stale on first show.
        ApplyImmersiveTheme(Hwnd, IsSystemDarkMode());
        if (Native.IsIconic(Hwnd))
        {
            Native.ShowWindow(Hwnd, Native.SW_RESTORE);
        }
        else
        {
            Native.ShowWindow(Hwnd, Native.SW_SHOW);
        }
        Native.BringWindowToTop(Hwnd);
        Native.SetForegroundWindow(Hwnd);
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
                // Persist size only when restored - skip min/maximize so
                // closing from maximized doesn't bake the maximized rect
                // as the new "normal" bounds.
                if (wParam.ToInt32() == Native.SIZE_RESTORED)
                {
                    _saveOnClose = true;
                }
                return IntPtr.Zero;

            case Native.WM_MOVE:
                _saveOnClose = true;
                return IntPtr.Zero;

            case Native.WM_GETMINMAXINFO:
                if (lParam != IntPtr.Zero)
                {
                    // Translate min CLIENT size to min OUTER size via
                    // AdjustWindowRectEx so the user can't drag the
                    // resize-grip below a usable layout width.
                    var minRect = new Native.RECT
                    {
                        Left = 0, Top = 0,
                        Right = MinClientWidth, Bottom = MinClientHeight,
                    };
                    Native.AdjustWindowRectEx(ref minRect,
                        Native.WS_OVERLAPPEDWINDOW, false, 0u);
                    var mmi = (Native.MINMAXINFO*)lParam;
                    mmi->ptMinTrackSize.x = minRect.Width;
                    mmi->ptMinTrackSize.y = minRect.Height;
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

            case Native.WM_CLOSE:
                // Fully tear down on close so the next open does a fresh
                // WebView2 init + navigation — picks up any newly-deployed
                // wwwroot. Saving bounds first; Program clears the
                // singleton + arms idle-exit inside OnDashboardClosed.
                if (_saveOnClose)
                {
                    SaveBounds();
                    _saveOnClose = false;
                }
                Program.OnDashboardClosed();
                Native.DestroyWindow(hwnd);
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
        // Same user-data-dir as OverlayWindow - this is what makes
        // Chromium reuse the existing browser/GPU/utility processes
        // instead of spawning a second tree.
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Qos", "DesktopWebView2");
        try { Directory.CreateDirectory(userDataDir); } catch { /* best-effort */ }

        _envCreatedHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);
        fixed (char* udf = userDataDir)
        {
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(
                null, udf, IntPtr.Zero, _envCreatedHandler);
            Log.Info($"dashboard CreateCoreWebView2Env hr=0x{hr:X8}");
            if (WebView2Native.Failed(hr))
            {
                Log.Error($"dashboard env init failed hr=0x{hr:X8}");
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
        Log.Info($"dashboard env created err=0x{errorCode:X8} env=0x{env:X}");
        if (WebView2Native.Failed(errorCode) || env == IntPtr.Zero) return;
        Wv2.AddRef(env);
        _env = env;
        Native.PostMessageW(Hwnd, WM_INIT_CONTROLLER, IntPtr.Zero, IntPtr.Zero);
    }

    private void InitController()
    {
        if (_env == IntPtr.Zero) { Log.Error("dashboard InitController: env null"); return; }
        _ctrlCreatedHandler = WebView2Callbacks.CreateControllerCreatedHandler(&OnControllerCreatedStatic);
        var hr = Wv2.Env_CreateCoreWebView2Controller(_env, Hwnd, _ctrlCreatedHandler);
        Log.Info($"dashboard CreateCoreWebView2Controller hr=0x{hr:X8}");
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
        Log.Info($"dashboard ctrl created err=0x{errorCode:X8} ctrl=0x{controller:X}");
        if (WebView2Native.Failed(errorCode) || controller == IntPtr.Zero)
        {
            Log.Error($"dashboard controller creation failed hr=0x{errorCode:X8}");
            return;
        }
        Wv2.AddRef(controller);
        _controller = controller;

        // Opaque white default so the page never shows the desktop or
        // transparent compositing behind it during navigation.
        _controller2 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller2);
        if (_controller2 != IntPtr.Zero)
        {
            Wv2.Ctrl2_put_DefaultBackgroundColor(_controller2, DefaultBgArgbOpaqueWhite);
        }

        Native.GetClientRect(Hwnd, out var rc);
        Wv2.Ctrl_put_Bounds(_controller, rc);
        Wv2.Ctrl_put_IsVisible(_controller, true);

        if (WebView2Native.Failed(Wv2.Ctrl_get_CoreWebView2(_controller, out _coreWebView2)) || _coreWebView2 == IntPtr.Zero)
        {
            Log.Error("dashboard get_CoreWebView2 failed");
            return;
        }

        if (WebView2Native.Succeeded(Wv2.Wv2_get_Settings(_coreWebView2, out var settings)) && settings != IntPtr.Zero)
        {
            Wv2.Settings_put_AreDefaultContextMenusEnabled(settings, true);
            Wv2.Settings_put_AreDevToolsEnabled(settings, true);
            Wv2.Settings_put_IsStatusBarEnabled(settings, true);
            Wv2.Settings_put_IsZoomControlEnabled(settings, true);
            Wv2.Release(settings);
        }

        // Feature parity with Edge --app: external links go to the OS
        // default browser, popup windows open externally, all WebAPI
        // permission prompts auto-deny.
        _navStartingHandler = WebView2Callbacks.CreateNavigationStartingHandler(&OnNavigationStartingStatic);
        var navHr = Wv2.Wv2_add_NavigationStarting(_coreWebView2, _navStartingHandler, out _navStartingToken);
        if (WebView2Native.Failed(navHr)) Log.Error($"dashboard add_NavigationStarting failed hr=0x{navHr:X8}");

        _newWindowHandler = WebView2Callbacks.CreateNewWindowRequestedHandler(&OnNewWindowRequestedStatic);
        var nwHr = Wv2.Wv2_add_NewWindowRequested(_coreWebView2, _newWindowHandler, out _newWindowToken);
        if (WebView2Native.Failed(nwHr)) Log.Error($"dashboard add_NewWindowRequested failed hr=0x{nwHr:X8}");

        _permissionHandler = WebView2Callbacks.CreatePermissionRequestedHandler(&OnPermissionRequestedStatic);
        var permHr = Wv2.Wv2_add_PermissionRequested(_coreWebView2, _permissionHandler, out _permissionToken);
        if (WebView2Native.Failed(permHr)) Log.Error($"dashboard add_PermissionRequested failed hr=0x{permHr:X8}");

        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"dashboard Navigate hr=0x{hr:X8} url={_navigationUrl}");

        // Reveal the window only after the controller is attached so
        // there's no flash of pre-WebView2 caption-bar-only frame.
        Native.ShowWindow(Hwnd, Native.SW_SHOWNORMAL);
        Native.BringWindowToTop(Hwnd);
        Native.SetForegroundWindow(Hwnd);
    }

    // ===================== Feature-parity event handlers =====================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationStartingStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNavStartingHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.NavStartingArgs_get_Uri(args, out var uri);
            if (!IsLocalDashboardUri(uri))
            {
                Wv2.NavStartingArgs_put_Cancel(args, true);
                ShellOpenExternal(uri);
                Log.Info($"dashboard external nav redirected to default browser: {uri}");
            }
        }
        catch (Exception ex) { Log.Error($"dashboard NavStarting: {ex.Message}"); }
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
            Log.Info($"dashboard target=_blank routed to default browser: {uri}");
        }
        catch (Exception ex) { Log.Error($"dashboard NewWindow: {ex.Message}"); }
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
            Log.Info($"dashboard permission denied kind={kind}");
        }
        catch (Exception ex) { Log.Error($"dashboard Permission: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    private static bool IsLocalDashboardUri(string? uri)
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
        try
        {
            Native.ShellExecuteW(IntPtr.Zero, "open", uri, null, null, Native.SW_SHOW);
        }
        catch (Exception ex) { Log.Error($"ShellExecute external: {ex.Message}"); }
    }

    // ===================== Bounds persistence =====================

    private static string BoundsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Qos", "dashboard-bounds.json");

    private (int x, int y, int w, int h) ResolveInitialBounds()
    {
        var saved = LoadSavedBounds();
        if (saved is not null) return saved.Value;

        // Default: center a DefaultClientWidth x DefaultClientHeight client
        // area on the primary monitor. AdjustWindowRectEx inflates to the
        // outer-frame size so the visible client matches what we asked for.
        var rc = new Native.RECT
        {
            Left = 0, Top = 0,
            Right = DefaultClientWidth, Bottom = DefaultClientHeight,
        };
        Native.AdjustWindowRectEx(ref rc, Native.WS_OVERLAPPEDWINDOW, false, 0u);
        var w = rc.Width;
        var h = rc.Height;
        var screenW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var screenH = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        var x = (screenW - w) / 2;
        var y = (screenH - h) / 2;
        return (x, y, w, h);
    }

    private static (int x, int y, int w, int h)? LoadSavedBounds()
    {
        try
        {
            var path = BoundsFilePath();
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var doc = JsonSerializer.Deserialize(stream, DashboardBoundsJson.Default.SavedBounds);
            if (doc is null) return null;
            // The stored bounds are outer-frame size. Refuse anything that
            // would land us under the documented client-area minimum,
            // including a corrupted/zeroed file.
            var minOuter = new Native.RECT { Left = 0, Top = 0, Right = MinClientWidth, Bottom = MinClientHeight };
            Native.AdjustWindowRectEx(ref minOuter, Native.WS_OVERLAPPEDWINDOW, false, 0u);
            if (doc.W < minOuter.Width || doc.H < minOuter.Height) return null;
            return (doc.X, doc.Y, doc.W, doc.H);
        }
        catch { return null; }
    }

    private void SaveBounds()
    {
        if (Hwnd == IntPtr.Zero) return;
        try
        {
            var wp = new Native.WINDOWPLACEMENT();
            wp.length = (uint)sizeof(Native.WINDOWPLACEMENT);
            if (!Native.GetWindowPlacement(Hwnd, ref wp)) return;
            // Always save the rcNormalPosition (the restored bounds) so
            // a maximized window doesn't restore to a maximized rect that
            // covers the taskbar after a Win32-only restore.
            var rc = wp.rcNormalPosition;
            var path = BoundsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new SavedBounds
            {
                X = rc.Left, Y = rc.Top,
                W = rc.Width, H = rc.Height,
            };
            using var fs = File.Create(path);
            JsonSerializer.Serialize(fs, data, DashboardBoundsJson.Default.SavedBounds);
        }
        catch (Exception ex) { Log.Error($"dashboard SaveBounds: {ex.Message}"); }
    }

    // ===================== Callback dispatch =====================

    private static DashboardWindow? FindByEnvHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._envCreatedHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByCtrlHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._ctrlCreatedHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByNavStartingHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._navStartingHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByNewWindowHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._newWindowHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByPermissionHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._permissionHandler == handler) return kv.Value;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.TryRemove(_instanceId, out _);
        try
        {
            // Revoke event subscriptions on the inner ICoreWebView2 before
            // closing the controller. Without this, a queued event firing
            // after Ctrl_Close would re-enter a handler whose backing
            // object we have already freed via Release below.
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
            // Release our local refs on the callback objects. The host
            // also AddRef'd them when we passed them into the add_* / env
            // creation calls; the matching Release was paired with the
            // remove_* above (for events) or fires automatically when the
            // host tears down (for env/controller-created handlers).
            if (_envCreatedHandler != IntPtr.Zero) { Wv2.Release(_envCreatedHandler); _envCreatedHandler = IntPtr.Zero; }
            if (_ctrlCreatedHandler != IntPtr.Zero) { Wv2.Release(_ctrlCreatedHandler); _ctrlCreatedHandler = IntPtr.Zero; }
            if (_navStartingHandler != IntPtr.Zero) { Wv2.Release(_navStartingHandler); _navStartingHandler = IntPtr.Zero; }
            if (_newWindowHandler != IntPtr.Zero) { Wv2.Release(_newWindowHandler); _newWindowHandler = IntPtr.Zero; }
            if (_permissionHandler != IntPtr.Zero) { Wv2.Release(_permissionHandler); _permissionHandler = IntPtr.Zero; }
        }
        catch (Exception ex) { Log.Error($"dashboard dispose: {ex.Message}"); }
        if (Hwnd != IntPtr.Zero)
        {
            Win32Window.Unregister(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }
}

internal sealed class SavedBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedBounds))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal partial class DashboardBoundsJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
