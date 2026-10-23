using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Native host for the Web View panel widget inside one kiosk window. Each
/// view is a WebView2 controller in its own child HWND of the kiosk, in a
/// separate environment and profile. All state is driven by the panel page's
/// bridge messages; every method runs on the kiosk's message-loop thread.
/// </summary>
internal sealed unsafe class WebViewWidgetHost : IWin32WindowOwner, IDisposable
{
    private const string ContainerClassName = "Nexus.Overlay.WebViewWidget";
    internal const uint WM_WIDGET_DEFERRED = Native.WM_USER + 4;
    internal static readonly UIntPtr TimerWatchdog = new(13);
    private const uint WatchdogPollMs = 500;
    private const uint WS_DISABLED = 0x08000000u;
    private const int PermissionStateDeny = 2;
    private const int WebErrorOperationCanceled = 14;
    private const int ProcessFailedBrowserExited = 0;
    private const int ProcessFailedRenderExited = 1;
    // COREWEBVIEW2_COLOR as a raw uint has alpha in the low byte.
    private const uint DefaultBgOpaqueWhite = 0xFFFFFFFFu;
    private const int MaxCursorShows = 64;

    private static readonly ConcurrentDictionary<long, WebViewWidgetHost> Hosts = new();
    private static readonly ConcurrentDictionary<long, NativeView> Views = new();
    private static long _nextKey;

    private sealed class NativeView
    {
        public required long Key;
        public required string Id;
        public required WebViewWidgetHost Host;
        public required WidgetViewSpec Spec;
        public IntPtr Container;
        public IntPtr Controller;
        public IntPtr Controller2;
        public IntPtr Core;
        public IntPtr Core4;
        public IntPtr CreatedHandler;
        public IntPtr NavStartingHandler;
        public IntPtr NavCompletedHandler;
        public IntPtr NewWindowHandler;
        public IntPtr PermissionHandler;
        public IntPtr ProcessFailedHandler;
        public IntPtr DownloadHandler;
        public long NavStartingToken;
        public long NavCompletedToken;
        public long NewWindowToken;
        public long PermissionToken;
        public long ProcessFailedToken;
        public long DownloadToken;
        public bool Ready;
        public bool Destroyed;
        public WidgetPixelRect AppliedRect;
        public bool AppliedShown;
        public bool AppliedInteractive;
        public double AppliedZoom;
        public int AppliedRadiusX;
        public int AppliedRadiusY;
        public readonly WidgetNavState Nav = new();
    }

    private readonly long _key = Interlocked.Increment(ref _nextKey);
    private readonly IntPtr _kioskHwnd;
    private readonly string _panelUrl;
    private readonly bool _compatibilityRendering;
    private readonly Func<string, bool> _post;
    private readonly WidgetRegistry _registry = new();
    private readonly Dictionary<string, NativeView> _byId = new(StringComparer.Ordinal);
    private readonly List<NativeView> _deferredDestroy = new();
    private IntPtr _env;
    private IntPtr _envOptions;
    private IntPtr _envHandler;
    private bool _envPending;
    private bool _timerRunning;
    private double _viewportW;
    private double _viewportH;
    private bool _activatable;
    private IntPtr _prevForeground;
    private bool _disposed;

    /// <param name="post">Delivers a JSON message to the panel page.</param>
    public WebViewWidgetHost(IntPtr kioskHwnd, string panelUrl, bool compatibilityRendering, Func<string, bool> post)
    {
        _kioskHwnd = kioskHwnd;
        _panelUrl = panelUrl;
        _compatibilityRendering = compatibilityRendering;
        _post = post;
        Hosts[_key] = this;
    }

    public int ViewCount => _byId.Count;

    // ===================== Bridge =====================

    /// <summary>Handles one raw message from the kiosk's WebMessageReceived; drops it unless it came from the panel origin.</summary>
    public void OnWebMessage(string? source, string? json)
    {
        if (_disposed || !WebViewWidgetProtocol.IsPanelOrigin(source, _panelUrl)) return;
        var msg = WebViewWidgetProtocol.Parse(json);
        switch (msg.Kind)
        {
            case WidgetMessageKind.Hello:
                DestroyAll("hello");
                Post(WebViewWidgetProtocol.BuildCaps(interactive: true));
                break;
            case WidgetMessageKind.Sync:
                ApplySync(msg);
                break;
            case WidgetMessageKind.Command:
                RunCommand(msg);
                break;
        }
    }

    private void ApplySync(WidgetMessage msg)
    {
        var viewportChanged = msg.ViewportW != _viewportW || msg.ViewportH != _viewportH;
        _viewportW = msg.ViewportW;
        _viewportH = msg.ViewportH;

        foreach (var action in _registry.Apply(msg, Environment.TickCount64))
        {
            switch (action.Kind)
            {
                case WidgetActionKind.Create:
                    CreateView(action.Spec!);
                    break;
                case WidgetActionKind.Navigate:
                    if (_byId.TryGetValue(action.Id, out var nav) && nav.Ready)
                    {
                        Wv2.Wv2_Navigate(nav.Core, action.Spec!.Url);
                    }
                    break;
                case WidgetActionKind.Layout:
                    if (_byId.TryGetValue(action.Id, out var lay))
                    {
                        lay.Spec = action.Spec!;
                        ApplyLayout(lay);
                    }
                    break;
                case WidgetActionKind.Destroy:
                    if (_byId.TryGetValue(action.Id, out var gone)) DestroyView(gone);
                    break;
                case WidgetActionKind.ReportError:
                    Post(WebViewWidgetProtocol.BuildState(action.Id, "error", action.Code, false));
                    break;
            }
        }

        if (viewportChanged) ApplyLayoutAll();
        UpdateTimer();
        ReleaseEnvIfIdle();
    }

    private void RunCommand(WidgetMessage msg)
    {
        if (!_byId.TryGetValue(msg.CommandId, out var view) || !view.Ready) return;
        if (msg.Command == "reload") Wv2.Wv2_Reload(view.Core);
        else if (Wv2.Wv2_get_CanGoBack(view.Core)) Wv2.Wv2_GoBack(view.Core);
    }

    private void Post(string json)
    {
        try { _post(json); }
        catch (Exception ex) { Log.Error($"webview-widget post: {ex.Message}"); }
    }

    private void PostState(NativeView view, string state, string? code = null)
    {
        view.Nav.OnState(state, code);
        var canGoBack = view.Ready && !view.Destroyed && Wv2.Wv2_get_CanGoBack(view.Core);
        Post(WebViewWidgetProtocol.BuildState(view.Id, state, code, canGoBack));
    }

    // ===================== Lifecycle =====================

    /// <summary>Destroys every view and releases the widget environment. Safe to call repeatedly.</summary>
    public void DestroyAll(string reason)
    {
        var any = _byId.Count > 0;
        _registry.Clear();
        foreach (var view in new List<NativeView>(_byId.Values)) DestroyView(view);
        UpdateTimer();
        ReleaseEnvIfIdle();
        if (any) Log.Info($"webview-widget destroyed all views reason={reason}");
    }

    // The kiosk is WS_EX_NOACTIVATE; while a view is interactive and shown that flag is dropped so a touch on it activates the kiosk and keystrokes reach the page.
    private void UpdateActivation()
    {
        var want = !_disposed;
        if (want)
        {
            want = false;
            foreach (var v in _byId.Values)
            {
                if (v.Ready && !v.Destroyed && v.AppliedShown && v.AppliedInteractive) { want = true; break; }
            }
        }
        if (want == _activatable)
        {
            RememberForeground();
            return;
        }
        _activatable = want;
        if (want) RememberForeground();
        else ShowCursorHiddenByTouch();
        if (!Native.IsWindow(_kioskHwnd)) return;
        var ex = Native.GetWindowLongW(_kioskHwnd, Native.GWL_EXSTYLE);
        ex = want ? ex & ~Native.WS_EX_NOACTIVATE : ex | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtrW(_kioskHwnd, Native.GWL_EXSTYLE, (IntPtr)ex);
        Native.SetWindowPos(_kioskHwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
        if (!want)
        {
            var prev = _prevForeground;
            _prevForeground = IntPtr.Zero;
            if (Native.GetForegroundWindow() == _kioskHwnd && prev != IntPtr.Zero && prev != _kioskHwnd
                && Native.IsWindow(prev) && Native.IsWindowVisible(prev) && !Native.IsIconic(prev))
            {
                Native.SetForegroundWindow(prev);
            }
            return;
        }
        if (Native.GetForegroundWindow() != _kioskHwnd) return;
        foreach (var v in _byId.Values)
        {
            if (v.Ready && v.AppliedShown && v.AppliedInteractive) { Wv2.Ctrl_MoveFocusProgrammatic(v.Controller); break; }
        }
    }

    // WebView2 hides the cursor on touch with ShowCursor and shows it again only on a mouse move over
    // the view, which a non-interactive view never gets. Its browser thread shares this thread's input
    // queue, so raising the count here makes the cursor visible over the overlay windows again.
    private static void ShowCursorHiddenByTouch()
    {
        // With no mouse attached Windows starts the count at -1; raising it would show a cursor on a touch-only box.
        if (Native.GetSystemMetrics(Native.SM_MOUSEPRESENT) == 0) return;
        var count = Native.ShowCursor(true);
        if (count > 0)
        {
            Native.ShowCursor(false);
            return;
        }
        var shows = 1;
        for (; count < 0 && shows < MaxCursorShows; shows++) count = Native.ShowCursor(true);
        Log.Info(count < 0
            ? $"webview-widget cursor still hidden after +{shows}, display count {count}"
            : $"webview-widget showed the cursor a touch had hidden, display count +{shows}");
    }

    // Last foreground window other than the kiosk, so focus can go back once no view is interactive.
    private void RememberForeground()
    {
        if (!_activatable) return;
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _kioskHwnd) _prevForeground = fg;
    }

    public void OnKioskResized() => ApplyLayoutAll();

    public void OnKioskMoved()
    {
        foreach (var view in _byId.Values)
        {
            if (view.Ready && !view.Destroyed && view.AppliedShown) Wv2.Ctrl_NotifyParentWindowPositionChanged(view.Controller);
        }
    }

    public void OnTimer(IntPtr timerId)
    {
        if (timerId != (IntPtr)(long)TimerWatchdog.ToUInt64()) return;
        if (_registry.WatchdogExpired(Environment.TickCount64)) DestroyAll("watchdog");
        else RememberForeground();
    }

    /// <summary>Destroys views queued by event handlers, which must not tear down inside their own callback.</summary>
    public void ProcessDeferred()
    {
        if (_disposed) return;
        var queued = _deferredDestroy.ToArray();
        _deferredDestroy.Clear();
        foreach (var view in queued)
        {
            if (!view.Destroyed) DestroyView(view);
        }
        UpdateTimer();
        ReleaseEnvIfIdle();
    }

    private void UpdateTimer()
    {
        var need = _registry.LiveCount > 0 && !_disposed;
        if (need == _timerRunning) return;
        _timerRunning = need;
        if (need) Native.SetTimer(_kioskHwnd, TimerWatchdog, WatchdogPollMs, IntPtr.Zero);
        else Native.KillTimer(_kioskHwnd, TimerWatchdog);
    }

    private void QueueDestroy(NativeView view)
    {
        _deferredDestroy.Add(view);
        Native.PostMessageW(_kioskHwnd, WM_WIDGET_DEFERRED, IntPtr.Zero, IntPtr.Zero);
    }

    // ===================== Environment =====================

    private void EnsureEnv()
    {
        if (_env != IntPtr.Zero || _envPending) return;
        var dir = UserDataPaths.Profile(_compatibilityRendering ? "PanelCompatWidgetWebView2" : "WidgetWebView2");
        try { Directory.CreateDirectory(dir); } catch { /* best-effort */ }

        if (_envOptions != IntPtr.Zero) { Wv2.Release(_envOptions); _envOptions = IntPtr.Zero; }
        if (_compatibilityRendering) _envOptions = Wv2EnvironmentOptions.Create(PanelKioskWindow.CompatibilityBrowserArguments);
        if (_envHandler != IntPtr.Zero) { Wv2.Release(_envHandler); _envHandler = IntPtr.Zero; }
        _envHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);
        WebView2Callbacks.SetUserData(_envHandler, _key);
        _envPending = true;
        fixed (char* udf = dir)
        {
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(null, udf, _envOptions, _envHandler);
            Log.Info($"webview-widget CreateCoreWebView2Env hr=0x{hr:X8} compat={_compatibilityRendering}");
            if (WebView2Native.Failed(hr))
            {
                _envPending = false;
                FailAllViews("env");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnvCreatedStatic(IntPtr self, int errorCode, IntPtr env)
    {
        try
        {
            if (Hosts.TryGetValue(WebView2Callbacks.GetUserData(self), out var host)) host.OnEnvCreated(errorCode, env);
        }
        catch (Exception ex) { Log.Error($"webview-widget env callback: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    private void OnEnvCreated(int errorCode, IntPtr env)
    {
        _envPending = false;
        Log.Info($"webview-widget env created err=0x{errorCode:X8}");
        if (WebView2Native.Failed(errorCode) || env == IntPtr.Zero)
        {
            FailAllViews("env");
            return;
        }
        if (_disposed || _byId.Count == 0) return;
        Wv2.AddRef(env);
        _env = env;
        foreach (var view in new List<NativeView>(_byId.Values))
        {
            if (!view.Ready && view.CreatedHandler == IntPtr.Zero) StartController(view);
        }
    }

    private void ReleaseEnvIfIdle()
    {
        if (_byId.Count > 0 || _envPending) return;
        if (_env != IntPtr.Zero) { Wv2.Release(_env); _env = IntPtr.Zero; }
        if (_envOptions != IntPtr.Zero) { Wv2.Release(_envOptions); _envOptions = IntPtr.Zero; }
        if (_envHandler != IntPtr.Zero) { Wv2.Release(_envHandler); _envHandler = IntPtr.Zero; }
    }

    private void FailAllViews(string code)
    {
        foreach (var view in new List<NativeView>(_byId.Values)) FailView(view, code);
    }

    private void FailView(NativeView view, string code)
    {
        _registry.MarkCrashed(view.Id, Environment.TickCount64);
        PostState(view, "error", code);
        QueueDestroy(view);
    }

    // ===================== View creation =====================

    private void CreateView(WidgetViewSpec spec)
    {
        IntPtr container;
        try
        {
            container = Win32Window.CreateChild(ContainerClassName,
                Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN | WS_DISABLED, 0,
                0, 0, 1, 1, _kioskHwnd, this);
        }
        catch (Exception ex)
        {
            Log.Error($"webview-widget container create failed: {ex.Message}");
            _registry.MarkCrashed(spec.Id, Environment.TickCount64);
            Post(WebViewWidgetProtocol.BuildState(spec.Id, "error", "create", false));
            return;
        }

        var view = new NativeView
        {
            Key = Interlocked.Increment(ref _nextKey),
            Id = spec.Id,
            Host = this,
            Spec = spec,
            Container = container,
        };
        Views[view.Key] = view;
        _byId[spec.Id] = view;
        Post(WebViewWidgetProtocol.BuildState(spec.Id, "loading", null, false));

        if (_env == IntPtr.Zero) EnsureEnv();
        else StartController(view);
    }

    private void StartController(NativeView view)
    {
        view.CreatedHandler = WebView2Callbacks.CreateControllerCreatedHandler(&OnControllerCreatedStatic);
        WebView2Callbacks.SetUserData(view.CreatedHandler, view.Key);
        var hr = Wv2.Env_CreateCoreWebView2Controller(_env, view.Container, view.CreatedHandler);
        if (WebView2Native.Failed(hr))
        {
            Log.Error($"webview-widget CreateCoreWebView2Controller hr=0x{hr:X8}");
            FailView(view, "create");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnControllerCreatedStatic(IntPtr self, int errorCode, IntPtr controller)
    {
        try
        {
            if (Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view))
            {
                view.Host.OnControllerCreated(view, errorCode, controller);
            }
            else if (controller != IntPtr.Zero)
            {
                // The view was destroyed while its controller was still being created.
                Wv2.Ctrl_Close(controller);
            }
        }
        catch (Exception ex) { Log.Error($"webview-widget controller callback: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    private void OnControllerCreated(NativeView view, int errorCode, IntPtr controller)
    {
        if (view.Destroyed)
        {
            if (controller != IntPtr.Zero) Wv2.Ctrl_Close(controller);
            return;
        }
        if (WebView2Native.Failed(errorCode) || controller == IntPtr.Zero)
        {
            Log.Error($"webview-widget controller creation failed hr=0x{errorCode:X8}");
            FailView(view, "create");
            return;
        }
        Wv2.AddRef(controller);
        view.Controller = controller;
        view.Controller2 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller2);
        if (view.Controller2 != IntPtr.Zero) Wv2.Ctrl2_put_DefaultBackgroundColor(view.Controller2, DefaultBgOpaqueWhite);

        if (WebView2Native.Failed(Wv2.Ctrl_get_CoreWebView2(controller, out view.Core)) || view.Core == IntPtr.Zero)
        {
            FailView(view, "create");
            return;
        }

        ConfigureSettings(view.Core);
        AttachHandlers(view);

        view.Ready = true;
        ApplyLayout(view);
        var url = _registry.ConfiguredUrl(view.Id) ?? view.Spec.Url;
        var hr = Wv2.Wv2_Navigate(view.Core, url);
        Log.Info($"webview-widget view ready navigate hr=0x{hr:X8} url={LogRedact.Url(url)}");
    }

    private static void ConfigureSettings(IntPtr core)
    {
        if (WebView2Native.Failed(Wv2.Wv2_get_Settings(core, out var settings)) || settings == IntPtr.Zero) return;
        Wv2.Settings_put_AreDefaultContextMenusEnabled(settings, false);
        Wv2.Settings_put_AreDevToolsEnabled(settings, false);
        Wv2.Settings_put_IsStatusBarEnabled(settings, false);
        Wv2.Settings_put_IsZoomControlEnabled(settings, false);
        Wv2.Settings_put_AreDefaultScriptDialogsEnabled(settings, false);
        Wv2.Settings_put_IsWebMessageEnabled(settings, false);
        Wv2.Settings_put_AreHostObjectsAllowed(settings, false);
        var settings3 = Wv2.QueryInterface(settings, WebView2Native.IID_ICoreWebView2Settings3);
        if (settings3 != IntPtr.Zero)
        {
            Wv2.Settings3_put_AreBrowserAcceleratorKeysEnabled(settings3, false);
            Wv2.Release(settings3);
        }
        var settings4 = Wv2.QueryInterface(settings, WebView2Native.IID_ICoreWebView2Settings4);
        if (settings4 != IntPtr.Zero)
        {
            Wv2.Settings4_put_IsPasswordAutosaveEnabled(settings4, false);
            Wv2.Settings4_put_IsGeneralAutofillEnabled(settings4, false);
            Wv2.Release(settings4);
        }
        Wv2.Release(settings);
    }

    private static void AttachHandlers(NativeView v)
    {
        v.NavStartingHandler = WebView2Callbacks.CreateNavigationStartingHandler(&OnNavigationStartingStatic);
        WebView2Callbacks.SetUserData(v.NavStartingHandler, v.Key);
        Wv2.Wv2_add_NavigationStarting(v.Core, v.NavStartingHandler, out v.NavStartingToken);

        v.NavCompletedHandler = WebView2Callbacks.CreateNavigationCompletedHandler(&OnNavigationCompletedStatic);
        WebView2Callbacks.SetUserData(v.NavCompletedHandler, v.Key);
        Wv2.Wv2_add_NavigationCompleted(v.Core, v.NavCompletedHandler, out v.NavCompletedToken);

        v.NewWindowHandler = WebView2Callbacks.CreateNewWindowRequestedHandler(&OnNewWindowRequestedStatic);
        WebView2Callbacks.SetUserData(v.NewWindowHandler, v.Key);
        Wv2.Wv2_add_NewWindowRequested(v.Core, v.NewWindowHandler, out v.NewWindowToken);

        v.PermissionHandler = WebView2Callbacks.CreatePermissionRequestedHandler(&OnPermissionRequestedStatic);
        WebView2Callbacks.SetUserData(v.PermissionHandler, v.Key);
        Wv2.Wv2_add_PermissionRequested(v.Core, v.PermissionHandler, out v.PermissionToken);

        v.ProcessFailedHandler = WebView2Callbacks.CreateProcessFailedHandler(&OnProcessFailedStatic);
        WebView2Callbacks.SetUserData(v.ProcessFailedHandler, v.Key);
        Wv2.Wv2_add_ProcessFailed(v.Core, v.ProcessFailedHandler, out v.ProcessFailedToken);

        v.Core4 = Wv2.QueryInterface(v.Core, WebView2Native.IID_ICoreWebView2_4);
        if (v.Core4 == IntPtr.Zero)
        {
            Log.Warn("webview-widget downloads not blocked: ICoreWebView2_4 unavailable");
            return;
        }
        v.DownloadHandler = WebView2Callbacks.CreateDownloadStartingHandler(&OnDownloadStartingStatic);
        WebView2Callbacks.SetUserData(v.DownloadHandler, v.Key);
        var hr = Wv2.Wv2_4_add_DownloadStarting(v.Core4, v.DownloadHandler, out v.DownloadToken);
        if (WebView2Native.Failed(hr)) Log.Warn($"webview-widget add_DownloadStarting failed hr=0x{hr:X8}");
    }

    // ===================== Layout and input =====================

    private void ApplyLayoutAll()
    {
        foreach (var view in _byId.Values) ApplyLayout(view);
    }

    private void ApplyLayout(NativeView view)
    {
        ApplyLayoutCore(view);
        UpdateActivation();
    }

    private void ApplyLayoutCore(NativeView view)
    {
        if (!view.Ready || view.Destroyed || _disposed) return;
        Native.GetClientRect(_kioskHwnd, out var client);
        var px = WebViewWidgetProtocol.MapRect(view.Spec.Rect, _viewportW, _viewportH, client.Width, client.Height);
        var show = view.Spec.Visible && !px.IsEmpty;

        if (view.AppliedZoom != view.Spec.Zoom)
        {
            Wv2.Ctrl_put_ZoomFactor(view.Controller, view.Spec.Zoom);
            view.AppliedZoom = view.Spec.Zoom;
        }

        if (!show)
        {
            if (view.AppliedShown)
            {
                Wv2.Ctrl_put_IsVisible(view.Controller, false);
                Native.ShowWindow(view.Container, Native.SW_HIDE);
                view.AppliedShown = false;
            }
            return;
        }

        if (view.AppliedInteractive != view.Spec.Interactive || !view.AppliedShown)
        {
            Native.EnableWindow(view.Container, view.Spec.Interactive);
            view.AppliedInteractive = view.Spec.Interactive;
        }

        var rectChanged = !view.AppliedShown || view.AppliedRect != px;
        if (rectChanged)
        {
            var flags = Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | (view.AppliedShown ? Native.SWP_NOZORDER : 0u);
            Native.SetWindowPos(view.Container, Native.HWND_TOP, px.Left, px.Top, px.Width, px.Height, flags);
            if (!view.AppliedShown || view.AppliedRect.Width != px.Width || view.AppliedRect.Height != px.Height)
            {
                Wv2.Ctrl_put_Bounds(view.Controller, new Native.RECT { Left = 0, Top = 0, Right = px.Width, Bottom = px.Height });
            }
            view.AppliedRect = px;
            Wv2.Ctrl_NotifyParentWindowPositionChanged(view.Controller);
        }

        var (rx, ry) = WebViewWidgetProtocol.MapRadius(view.Spec.Radius, _viewportW, _viewportH, client.Width, client.Height);
        if (rectChanged || rx != view.AppliedRadiusX || ry != view.AppliedRadiusY)
        {
            ApplyRegion(view, px, rx, ry);
            view.AppliedRadiusX = rx;
            view.AppliedRadiusY = ry;
        }

        if (!view.AppliedShown)
        {
            Wv2.Ctrl_put_IsVisible(view.Controller, true);
            view.AppliedShown = true;
        }
    }

    private static void ApplyRegion(NativeView view, WidgetPixelRect px, int rx, int ry)
    {
        if (rx <= 0 && ry <= 0)
        {
            if (view.AppliedRadiusX > 0 || view.AppliedRadiusY > 0) Native.SetWindowRgn(view.Container, IntPtr.Zero, true);
            return;
        }
        // Right and bottom are exclusive in GDI regions, hence the +1.
        var rgn = Native.CreateRoundRectRgn(0, 0, px.Width + 1, px.Height + 1, rx * 2, ry * 2);
        if (rgn == IntPtr.Zero) return;
        if (Native.SetWindowRgn(view.Container, rgn, true) == 0) Native.DeleteObject(rgn);
    }

    // A disabled child window is skipped by hit-testing, so non-interactive input reaches the panel webview beneath.
    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
                return (IntPtr)Native.HTTRANSPARENT;
            case Native.WM_ERASEBKGND:
                return (IntPtr)1;
        }
        return null;
    }

    // ===================== Event handlers =====================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationStartingStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (args == IntPtr.Zero || !Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view)) return WebView2Native.S_OK;
            Wv2.NavStartingArgs_get_Uri(args, out var uri);
            if (!WebViewWidgetProtocol.IsHttpScheme(uri))
            {
                Wv2.NavStartingArgs_put_Cancel(args, true);
                Log.Info($"webview-widget blocked navigation {LogRedact.Url(uri)}");
                return WebView2Native.S_OK;
            }
            view.Nav.OnNavigationStarting();
            view.Host.PostState(view, "loading");
        }
        catch (Exception ex) { Log.Error($"webview-widget NavStarting: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationCompletedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (!Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view)) return WebView2Native.S_OK;
            var ok = false;
            var status = 0;
            if (args != IntPtr.Zero)
            {
                Wv2.NavCompletedArgs_get_IsSuccess(args, out ok);
                Wv2.NavCompletedArgs_get_WebErrorStatus(args, out status);
            }
            if (ok)
            {
                // Navigation can reset the page zoom; re-assert the configured one.
                Wv2.Ctrl_put_ZoomFactor(view.Controller, view.Spec.Zoom);
                view.Host.PostState(view, "loaded");
            }
            else if (status == WebErrorOperationCanceled) return WebView2Native.S_OK;
            else if (view.Nav.TryConsumeDownloadFailure())
            {
                // The page is unchanged after a blocked download; restore the state the loading event replaced.
                if (view.Nav.SettledState is { } prev) view.Host.PostState(view, prev, view.Nav.SettledCode);
            }
            else view.Host.PostState(view, "error", status.ToString());
        }
        catch (Exception ex) { Log.Error($"webview-widget NavCompleted: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNewWindowRequestedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (args == IntPtr.Zero || !Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view)) return WebView2Native.S_OK;
            Wv2.NewWindowArgs_get_Uri(args, out var uri);
            Wv2.NewWindowArgs_put_Handled(args, true);
            if (WebViewWidgetProtocol.IsHttpScheme(uri)) Wv2.Wv2_Navigate(view.Core, uri!);
        }
        catch (Exception ex) { Log.Error($"webview-widget NewWindow: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnPermissionRequestedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (args != IntPtr.Zero) Wv2.PermissionArgs_put_State(args, PermissionStateDeny);
        }
        catch (Exception ex) { Log.Error($"webview-widget Permission: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDownloadStartingStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (args == IntPtr.Zero) return WebView2Native.S_OK;
            if (Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view)) view.Nav.OnDownloadCancelled();
            Wv2.DownloadArgs_put_Cancel(args, true);
            Wv2.DownloadArgs_put_Handled(args, true);
        }
        catch (Exception ex) { Log.Error($"webview-widget Download: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnProcessFailedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (!Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view)) return WebView2Native.S_OK;
            var kind = -1;
            if (args != IntPtr.Zero && WebView2Native.Succeeded(Wv2.ProcessFailedArgs_get_Kind(args, out var read))) kind = read;
            view.Host.OnViewProcessFailed(view, kind);
        }
        catch (Exception ex) { Log.Error($"webview-widget ProcessFailed: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    private void OnViewProcessFailed(NativeView view, int kind)
    {
        Log.Warn($"webview-widget process failed kind={kind}");
        if (kind != ProcessFailedBrowserExited && kind != ProcessFailedRenderExited) return;
        if (kind == ProcessFailedBrowserExited)
        {
            // The shared browser process is gone, so every view is dead.
            foreach (var other in new List<NativeView>(_byId.Values)) Crash(other);
            return;
        }
        Crash(view);
    }

    private void Crash(NativeView view)
    {
        if (view.Destroyed) return;
        _registry.MarkCrashed(view.Id, Environment.TickCount64);
        PostState(view, "crashed");
        QueueDestroy(view);
    }

    // ===================== Teardown =====================

    private void DestroyView(NativeView view)
    {
        if (view.Destroyed) return;
        view.Destroyed = true;
        Views.TryRemove(view.Key, out _);
        if (_byId.TryGetValue(view.Id, out var current) && ReferenceEquals(current, view)) _byId.Remove(view.Id);

        try
        {
            if (view.Core != IntPtr.Zero)
            {
                if (view.NavStartingToken != 0) Wv2.Wv2_remove_NavigationStarting(view.Core, view.NavStartingToken);
                if (view.NavCompletedToken != 0) Wv2.Wv2_remove_NavigationCompleted(view.Core, view.NavCompletedToken);
                if (view.NewWindowToken != 0) Wv2.Wv2_remove_NewWindowRequested(view.Core, view.NewWindowToken);
                if (view.PermissionToken != 0) Wv2.Wv2_remove_PermissionRequested(view.Core, view.PermissionToken);
                if (view.ProcessFailedToken != 0) Wv2.Wv2_remove_ProcessFailed(view.Core, view.ProcessFailedToken);
            }
            if (view.Core4 != IntPtr.Zero)
            {
                if (view.DownloadToken != 0) Wv2.Wv2_4_remove_DownloadStarting(view.Core4, view.DownloadToken);
                Wv2.Release(view.Core4);
            }
            if (view.Controller != IntPtr.Zero)
            {
                Wv2.Ctrl_Close(view.Controller);
                Wv2.Release(view.Controller);
            }
            if (view.Core != IntPtr.Zero) Wv2.Release(view.Core);
            if (view.Controller2 != IntPtr.Zero) Wv2.Release(view.Controller2);
            ReleaseHandler(view.CreatedHandler);
            ReleaseHandler(view.NavStartingHandler);
            ReleaseHandler(view.NavCompletedHandler);
            ReleaseHandler(view.NewWindowHandler);
            ReleaseHandler(view.PermissionHandler);
            ReleaseHandler(view.ProcessFailedHandler);
            ReleaseHandler(view.DownloadHandler);
        }
        catch (Exception ex) { Log.Error($"webview-widget destroy: {ex.Message}"); }
        view.Core = view.Core4 = view.Controller = view.Controller2 = IntPtr.Zero;

        if (view.Container != IntPtr.Zero)
        {
            if (Native.IsWindow(view.Container)) Native.DestroyWindow(view.Container);
            Win32Window.Unregister(view.Container);
            view.Container = IntPtr.Zero;
        }
        UpdateActivation();
    }

    private static void ReleaseHandler(IntPtr handler)
    {
        if (handler != IntPtr.Zero) Wv2.Release(handler);
    }

    public void Dispose()
    {
        if (_disposed) return;
        DestroyAll("dispose");
        _disposed = true;
        if (_timerRunning)
        {
            Native.KillTimer(_kioskHwnd, TimerWatchdog);
            _timerRunning = false;
        }
        _deferredDestroy.Clear();
        Hosts.TryRemove(_key, out _);
        if (_envPending) Log.Info("webview-widget disposed with environment creation pending");
        if (_envHandler != IntPtr.Zero) { Wv2.Release(_envHandler); _envHandler = IntPtr.Zero; }
    }
}
