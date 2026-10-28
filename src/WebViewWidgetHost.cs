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
    internal static readonly UIntPtr TimerBlurGrace = new(14);
    internal static readonly UIntPtr TimerBackSwipe = new(15);
    internal static readonly UIntPtr TimerNavError = new(16);
    private const uint BackDeferMs = 150;
    private const uint NavErrorGraceMs = 300;

    /// <summary>True for the timer ids this host owns.</summary>
    internal static bool OwnsTimer(IntPtr id)
        => id == (IntPtr)(long)TimerWatchdog.ToUInt64() || id == (IntPtr)(long)TimerBlurGrace.ToUInt64()
           || id == (IntPtr)(long)TimerBackSwipe.ToUInt64() || id == (IntPtr)(long)TimerNavError.ToUInt64();
    private const uint WatchdogPollMs = 500;
    private const uint WS_DISABLED = 0x08000000u;
    private const int PermissionStateDeny = 2;
    private const int WebErrorOperationCanceled = 14;
    private const int ProcessFailedBrowserExited = 0;
    private const int ProcessFailedRenderExited = 1;
    // COREWEBVIEW2_COLOR as a raw uint has alpha in the low byte.
    private const uint DefaultBgOpaqueWhite = 0xFFFFFFFFu;
    private const int MaxCursorShows = 64;
    private const long CursorRestoreIntervalMs = 250;
    private const long BackSwipeGapMs = 400;

    private static readonly ConcurrentDictionary<long, WebViewWidgetHost> Hosts = new();
    private static readonly ConcurrentDictionary<long, NativeView> Views = new();
    private static long _nextKey;

    private sealed class NativeView
    {
        public required long Key;
        public required string Id;
        public required WebViewWidgetHost Host;
        public required WidgetViewSpec Spec;
        public readonly LongPressGate LongPress = new();
        public readonly LongPressGate BackSwipe = new(BackSwipeGapMs);
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
        public IntPtr WebMessageHandler;
        public long NavStartingToken;
        public long NavCompletedToken;
        public long NewWindowToken;
        public long PermissionToken;
        public long ProcessFailedToken;
        public long DownloadToken;
        public long WebMessageToken;
        public bool Ready;
        public bool Destroyed;
        public WidgetPixelRect AppliedRect;
        public bool AppliedShown;
        public bool AppliedInteractive;
        public double AppliedZoom;
        public int AppliedRadiusX;
        public int AppliedRadiusY;
        public readonly WidgetNavState Nav = new();
        public readonly WidgetHomeTracker Home = new();
        public readonly WidgetEditFocus EditFocus = new();
    }

    private readonly long _key = Interlocked.Increment(ref _nextKey);
    private readonly IntPtr _kioskHwnd;
    private readonly string _panelUrl;
    private readonly bool _compatibilityRendering;
    private readonly Func<string, bool> _post;
    private readonly WidgetRegistry _registry = new();
    private readonly Dictionary<string, NativeView> _byId = new(StringComparer.Ordinal);
    private readonly List<NativeView> _deferredDestroy = new();
    private readonly List<NativeView> _pendingBack = new();
    private IntPtr _env;
    private IntPtr _envOptions;
    private IntPtr _envHandler;
    private bool _envPending;
    private bool _timerRunning;
    private double _viewportW;
    private double _viewportH;
    private bool _interactive;
    private int? _lastStuckCount;
    private CursorRestoreThrottle _cursorRestoreThrottle = new(CursorRestoreIntervalMs);
    private readonly Action _onRealMouseMove;
    private bool _activatable;
    private NativeView? _lastEditing;
    private readonly Action<bool> _setEditing;
    private bool _disposed;

    /// <param name="post">Delivers a JSON message to the panel page.</param>
    /// <param name="setEditing">Reports whether a widget text field is being edited; the kiosk owns the activation style.</param>
    public WebViewWidgetHost(IntPtr kioskHwnd, string panelUrl, bool compatibilityRendering, Func<string, bool> post, Action<bool> setEditing)
    {
        _setEditing = setEditing;
        _kioskHwnd = kioskHwnd;
        _panelUrl = panelUrl;
        _compatibilityRendering = compatibilityRendering;
        _post = post;
        Hosts[_key] = this;
        _onRealMouseMove = OnRealMouseMove;
        TouchCursorGuard.RealMouseMoved += _onRealMouseMove;
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
                        nav.Home.Expect();
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
        else if (msg.Command == "home") NavigateHome(view);
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
        var live = view.Ready && !view.Destroyed;
        var canGoBack = live && Wv2.Wv2_get_CanGoBack(view.Core);
        var away = live && view.Home.IsAway(Wv2.Wv2_get_Source(view.Core));
        Post(WebViewWidgetProtocol.BuildState(view.Id, state, code, canGoBack, away));
        // A page in error runs no scripts, so only a loaded page can draw the disc.
        if (live && state == "loaded") Wv2.Wv2_PostWebMessageAsJson(view.Core, WebViewWidgetProtocol.BuildNavHint(canGoBack || away));
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

    // The kiosk stays WS_EX_NOACTIVATE so a touch on it never takes the foreground (a fullscreen game drops out of
    // fullscreen); only while a text field in an interactive view has focus is the flag dropped and the kiosk focused.
    private void UpdateActivation()
    {
        var interactive = false;
        foreach (var v in _byId.Values)
        {
            var live = !_disposed && IsLive(v);
            v.EditFocus.SetLive(live);
            interactive |= live;
        }
        if (_interactive != interactive) _lastStuckCount = null;
        if (_interactive != interactive) Log.Info($"webview-widget interactive views {(interactive ? "entered" : "left")}");
        if (_interactive && !interactive) ClampCursorCount("last interactive view gone");
        _interactive = interactive;

        var want = EditingView() is not null;
        if (want == _activatable) return;
        _activatable = want;
        if (!Native.IsWindow(_kioskHwnd)) return;
        _setEditing(want);
        if (!want) return;
        if (Native.GetForegroundWindow() != _kioskHwnd && !Native.SetForegroundWindow(_kioskHwnd))
        {
            Log.Warn("webview-widget text field focused but the kiosk could not take the foreground");
            OnKioskDeactivated();
            return;
        }
        FocusEditingView();
    }

    /// <summary>Moves keyboard focus to the view whose text field asked for it; false when none did.</summary>
    public bool FocusEditingView()
    {
        if (!_activatable || EditingView() is not { } v) return false;
        // The touch already focused the view; MoveFocus would blur and refocus the page, which reports the blur.
        if (!Native.IsChild(v.Container, Native.GetFocus())) Wv2.Ctrl_MoveFocusProgrammatic(v.Controller);
        return true;
    }

    // The view that most recently reported a focused text field wins over any other still focused one.
    private NativeView? EditingView()
    {
        if (_lastEditing is { Destroyed: false } last && last.EditFocus.Focused && IsLive(last)) return last;
        foreach (var v in _byId.Values)
        {
            if (v.EditFocus.Focused && IsLive(v)) return v;
        }
        return null;
    }

    public void OnKioskDeactivated()
    {
        if (!_activatable) return;
        foreach (var v in _byId.Values) v.EditFocus.SetLive(false);
        UpdateActivation();
    }

    private static bool IsLive(NativeView v) => v.Ready && !v.Destroyed && v.AppliedShown && v.AppliedInteractive;

    private void OnEditFocus(NativeView view, bool focused)
    {
        if (view.Destroyed || _disposed) return;
        view.EditFocus.SetLive(IsLive(view));
        view.EditFocus.OnReport(focused, Environment.TickCount64);
        if (view.EditFocus.Focused) _lastEditing = view;
        ArmBlurTimer();
        UpdateActivation();
    }

    // WebView2 hides the cursor on touch with ShowCursor and shows it again only on a mouse move over
    // the view. A non-interactive view never gets one, and with the mouse kept off the panel an interactive
    // one does not either. The browser's UI thread is input-attached to this (the kiosk message-loop) thread,
    // so they share one display count. Clamping it to zero here makes the cursor visible over the overlay
    // windows again, and keeps it from drifting above zero when the mouse later reaches the view and
    // Chromium shows the cursor itself, which would stop the next touch from hiding it.
    // Runs on this thread when the last interactive view goes (backstop) and on a real mouse move.
    private void ClampCursorCount(string reason, bool quietWhenClean = false)
    {
        // With no mouse attached Windows starts the count at -1; raising it would show a cursor on a touch-only box.
        if (Native.GetSystemMetrics(Native.SM_MOUSEPRESENT) == 0) return;
        // The probe moves the count by one; the loop below starts from the probed value.
        var count = Native.ShowCursor(true);
        var start = count - 1;
        var calls = 1;
        while (calls < MaxCursorShows && CursorCountClamp.NextCall(count) is { } show)
        {
            count = Native.ShowCursor(show);
            calls++;
        }
        if (start == 0)
        {
            if (!quietWhenClean) Log.Info($"webview-widget cursor count already 0 ({reason})");
            _lastStuckCount = null;
            return;
        }
        if (count != 0)
        {
            if (_lastStuckCount != count) Log.Info($"webview-widget cursor count stuck at {count} from {start} ({reason})");
            _lastStuckCount = count;
            return;
        }
        _lastStuckCount = null;
        Log.Info($"webview-widget cursor count clamped from {start} to 0 ({reason})");
    }

    private void OnRealMouseMove()
    {
        if (_disposed || !_interactive) return;
        var now = Environment.TickCount64;
        if (!_cursorRestoreThrottle.TryAcquire(now)) return;
        ClampCursorCount("real mouse move", quietWhenClean: true);
    }

    public void OnKioskResized() => ApplyLayoutAll();

    public void OnKioskMoved()
    {
        foreach (var view in _byId.Values)
        {
            if (view.Ready && !view.Destroyed && view.AppliedShown) Wv2.Ctrl_NotifyParentWindowPositionChanged(view.Controller);
        }
    }

    private void ArmBlurTimer()
    {
        long? next = null;
        foreach (var v in _byId.Values)
        {
            if (v.EditFocus.BlurDeadline is { } d && (next is null || d < next)) next = d;
        }
        if (next is not { } deadline)
        {
            Native.KillTimer(_kioskHwnd, TimerBlurGrace);
            return;
        }
        Native.SetTimer(_kioskHwnd, TimerBlurGrace, (uint)Math.Max(1, deadline - Environment.TickCount64), IntPtr.Zero);
    }

    public void OnTimer(IntPtr timerId)
    {
        if (timerId == (IntPtr)(long)TimerBlurGrace.ToUInt64())
        {
            var now = Environment.TickCount64;
            var expired = false;
            foreach (var v in _byId.Values) expired |= v.EditFocus.ExpireBlur(now);
            ArmBlurTimer();
            if (expired) UpdateActivation();
            return;
        }
        if (timerId == (IntPtr)(long)TimerBackSwipe.ToUInt64())
        {
            Native.KillTimer(_kioskHwnd, TimerBackSwipe);
            var due = _pendingBack.ToArray();
            _pendingBack.Clear();
            foreach (var v in due)
            {
                if (!v.Destroyed && v.Ready && Wv2.Wv2_get_CanGoBack(v.Core)) Wv2.Wv2_GoBack(v.Core);
            }
            return;
        }
        if (timerId == (IntPtr)(long)TimerNavError.ToUInt64())
        {
            Native.KillTimer(_kioskHwnd, TimerNavError);
            foreach (var v in new List<NativeView>(_byId.Values))
            {
                if (!v.Destroyed) ApplyNavFailure(v, v.Nav.ResolvePending());
            }
            return;
        }
        if (timerId != (IntPtr)(long)TimerWatchdog.ToUInt64()) return;
        if (_registry.WatchdogExpired(Environment.TickCount64)) DestroyAll("watchdog");
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
        var dir = UserDataPaths.Profile(
            _compatibilityRendering ? "PanelCompatWidgetWebView2" : "WidgetWebView2", ProfileMigration.WidgetState);
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
        view.Home.Expect();
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
        // Carries only the edit-focus, long-press and back-swipe reports; OnWebMessageReceivedStatic accepts nothing else.
        Wv2.Settings_put_IsWebMessageEnabled(settings, true);
        Wv2.Settings_put_AreHostObjectsAllowed(settings, false);
        var settings3 = Wv2.QueryInterface(settings, WebView2Native.IID_ICoreWebView2Settings3);
        if (settings3 != IntPtr.Zero)
        {
            Wv2.Settings3_put_AreBrowserAcceleratorKeysEnabled(settings3, false);
            Wv2.Release(settings3);
        }
        // The browser's overscroll navigation showed its arrow without going back here; BackSwipeScript replaces it.
        var settings6 = Wv2.QueryInterface(settings, WebView2Native.IID_ICoreWebView2Settings6);
        if (settings6 != IntPtr.Zero)
        {
            var swipeHr = Wv2.Settings6_put_IsSwipeNavigationEnabled(settings6, false);
            Log.Info($"webview-widget swipe navigation off hr=0x{swipeHr:X8}");
            Wv2.Release(settings6);
        }
        else Log.Warn("webview-widget Settings6 unavailable: browser swipe navigation stays on");
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

        v.WebMessageHandler = WebView2Callbacks.CreateWebMessageReceivedHandler(&OnWebMessageReceivedStatic);
        WebView2Callbacks.SetUserData(v.WebMessageHandler, v.Key);
        Wv2.Wv2_add_WebMessageReceived(v.Core, v.WebMessageHandler, out v.WebMessageToken);
        var scriptHr = Wv2.Wv2_AddScriptToExecuteOnDocumentCreated(v.Core, WebViewWidgetProtocol.EditFocusScript);
        if (WebView2Native.Failed(scriptHr)) Log.Warn($"webview-widget edit-focus script hr=0x{scriptHr:X8}");
        var backHr = Wv2.Wv2_AddScriptToExecuteOnDocumentCreated(v.Core, WebViewWidgetProtocol.BackSwipeScript);
        if (WebView2Native.Failed(backHr)) Log.Warn($"webview-widget back-swipe script hr=0x{backHr:X8}");
        var pressHr = Wv2.Wv2_AddScriptToExecuteOnDocumentCreated(v.Core, WebViewWidgetProtocol.LongPressScript);
        if (WebView2Native.Failed(pressHr)) Log.Warn($"webview-widget long-press script hr=0x{pressHr:X8}");

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
            // Windows notifies the container when a touch or click lands on the view's browser window.
            case Native.WM_PARENTNOTIFY:
                var evt = (uint)((long)wParam & 0xFFFF);
                if (evt == Native.WM_RBUTTONDOWN)
                {
                    // A right-click opens the widget menu, so it never authorizes a text field's focus.
                    foreach (var v in _byId.Values) v.EditFocus.Disarm();
                    OnRightButtonDown(hwnd, lParam);
                    return null;
                }
                if (evt == Native.WM_POINTERDOWN || evt == Native.WM_LBUTTONDOWN)
                {
                    foreach (var v in _byId.Values)
                    {
                        if (v.Container == hwnd) v.EditFocus.OnPointerDown(Environment.TickCount64);
                        else v.EditFocus.Disarm();
                    }
                }
                return null;
            case Native.WM_NCHITTEST:
                return (IntPtr)Native.HTTRANSPARENT;
            case Native.WM_ERASEBKGND:
                return (IntPtr)1;
        }
        return null;
    }

    // An interactive view takes the mouse, so the page's widget menu opens from the host's report of the right-click.
    private void OnRightButtonDown(IntPtr container, IntPtr lParam)
    {
        NativeView? view = null;
        foreach (var v in _byId.Values) if (v.Container == container) view = v;
        if (view is null || !IsLive(view)) return;
        // lParam is the cursor in the container's client area, as signed 16-bit words.
        ReportMenu(view, (short)((long)lParam & 0xFFFF), (short)(((long)lParam >> 16) & 0xFFFF));
    }

    // The page reported a rightward swipe: step back in its history, doing nothing when there is none.
    private void OnBackSwipe(NativeView view)
    {
        if (view.Destroyed || _disposed || !view.Ready) return;
        if (!view.BackSwipe.Accept(IsLive(view), view.Spec.Interactive, Environment.TickCount64)) return;
        if (!Wv2.Wv2_get_CanGoBack(view.Core) || _pendingBack.Contains(view)) return;
        // One short tick lets any residual overscroll animation end before the document swaps.
        _pendingBack.Add(view);
        Native.SetTimer(_kioskHwnd, TimerBackSwipe, BackDeferMs, IntPtr.Zero);
    }

    // The page's home button: back to the configured address, wherever the history stands.
    private void OnHomeRequest(NativeView view)
    {
        if (view.Destroyed || _disposed || !view.Ready) return;
        if (!view.BackSwipe.Accept(IsLive(view), view.Spec.Interactive, Environment.TickCount64)) return;
        NavigateHome(view);
    }

    private void NavigateHome(NativeView view)
    {
        var url = _registry.ConfiguredUrl(view.Id) ?? view.Spec.Url;
        view.Home.Expect();
        Wv2.Wv2_Navigate(view.Core, url);
    }

    private void ApplyNavFailure(NativeView view, NavFailureAction action)
    {
        if (action == NavFailureAction.Restore && view.Nav.SettledState is { } prev) PostState(view, prev, view.Nav.SettledCode);
        else if (action == NavFailureAction.Report) PostState(view, "error", view.Nav.FailureCode);
    }

    // A touch held in place inside the page opens the same menu as a right-click.
    private void OnLongPress(NativeView view, double fx, double fy)
    {
        if (view.Destroyed || _disposed) return;
        if (!view.LongPress.Accept(IsLive(view), view.Spec.Interactive, Environment.TickCount64)) return;
        foreach (var v in _byId.Values) v.EditFocus.Disarm();
        ReportMenu(view, (int)Math.Round(fx * view.AppliedRect.Width), (int)Math.Round(fy * view.AppliedRect.Height));
    }

    // x and y are pixels inside the view's container.
    private void ReportMenu(NativeView view, int x, int y)
    {
        Native.GetClientRect(_kioskHwnd, out var client);
        if (WebViewWidgetProtocol.UnmapPoint(view.AppliedRect.Left + x, view.AppliedRect.Top + y, _viewportW, _viewportH, client.Width, client.Height) is not { } css) return;
        Post(WebViewWidgetProtocol.BuildContextMenu(view.Id, css.X, css.Y));
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
            view.EditFocus.OnNavigationStarting();
            view.Host.UpdateActivation();
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
                view.Home.OnCompleted(Wv2.Wv2_get_Source(view.Core));
                view.Host.PostState(view, "loaded");
            }
            else if (status == WebErrorOperationCanceled) return WebView2Native.S_OK;
            else
            {
                // A blocked download leaves the page unchanged; its notice can trail this completion, so an unexplained failure waits briefly.
                view.Home.OnFailed();
                var verdict = view.Nav.OnNavigationFailed(status.ToString());
                if (verdict == NavFailureAction.Defer) Native.SetTimer(view.Host._kioskHwnd, TimerNavError, NavErrorGraceMs, IntPtr.Zero);
                else view.Host.ApplyNavFailure(view, verdict);
            }
        }
        catch (Exception ex) { Log.Error($"webview-widget NavCompleted: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnWebMessageReceivedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            if (args == IntPtr.Zero || !Views.TryGetValue(WebView2Callbacks.GetUserData(self), out var view)) return WebView2Native.S_OK;
            var json = Wv2.WebMsgArgs_GetJson(args);
            if (WebViewWidgetProtocol.ParseEditFocus(json) is { } focused) view.Host.OnEditFocus(view, focused);
            else if (WebViewWidgetProtocol.IsBackSwipe(json)) view.Host.OnBackSwipe(view);
            else if (WebViewWidgetProtocol.IsHomeRequest(json)) view.Host.OnHomeRequest(view);
            else if (WebViewWidgetProtocol.ParseLongPress(json) is { } press) view.Host.OnLongPress(view, press.X, press.Y);
        }
        catch (Exception ex) { Log.Error($"webview-widget WebMessage: {ex.Message}"); }
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
        if (ReferenceEquals(_lastEditing, view)) _lastEditing = null;

        try
        {
            if (view.Core != IntPtr.Zero)
            {
                if (view.NavStartingToken != 0) Wv2.Wv2_remove_NavigationStarting(view.Core, view.NavStartingToken);
                if (view.NavCompletedToken != 0) Wv2.Wv2_remove_NavigationCompleted(view.Core, view.NavCompletedToken);
                if (view.NewWindowToken != 0) Wv2.Wv2_remove_NewWindowRequested(view.Core, view.NewWindowToken);
                if (view.PermissionToken != 0) Wv2.Wv2_remove_PermissionRequested(view.Core, view.PermissionToken);
                if (view.ProcessFailedToken != 0) Wv2.Wv2_remove_ProcessFailed(view.Core, view.ProcessFailedToken);
                if (view.WebMessageToken != 0) Wv2.Wv2_remove_WebMessageReceived(view.Core, view.WebMessageToken);
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
            ReleaseHandler(view.WebMessageHandler);
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
        TouchCursorGuard.RealMouseMoved -= _onRealMouseMove;
        DestroyAll("dispose");
        _disposed = true;
        if (_timerRunning)
        {
            Native.KillTimer(_kioskHwnd, TimerWatchdog);
            _timerRunning = false;
        }
        Native.KillTimer(_kioskHwnd, TimerBlurGrace);
        Native.KillTimer(_kioskHwnd, TimerBackSwipe);
        Native.KillTimer(_kioskHwnd, TimerNavError);
        _pendingBack.Clear();
        _deferredDestroy.Clear();
        Hosts.TryRemove(_key, out _);
        if (_envPending) Log.Info("webview-widget disposed with environment creation pending");
        if (_envHandler != IntPtr.Zero) { Wv2.Release(_envHandler); _envHandler = IntPtr.Zero; }
    }
}
