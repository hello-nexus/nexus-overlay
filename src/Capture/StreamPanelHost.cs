using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay.Capture;

/// <summary>
/// One streamed-panel session: an off-screen WebView2 rendering
/// <c>/panel/{id}</c>, captured via Windows.Graphics.Capture on a poll
/// thread, hardware-encoded to H.264 (<see cref="MfEncoder"/>), and pushed
/// to the service ingest (<see cref="IngestClient"/>). The service owns
/// pacing and the device transport; this host only produces frames.
///
/// Uses its own WebView2 environment (StreamWebView2 user-data folder) so
/// the stream browser arguments never touch the dashboard /
/// kiosk / overlay surfaces sharing DesktopWebView2.
/// </summary>
internal sealed unsafe class StreamPanelHost : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.StreamHost";
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 7;
    private const uint WM_HOST_FAULTED = Native.WM_USER + 8;
    private const uint WM_TOUCH_CONTACTS = Native.WM_USER + 9;
    private const int ProcessFailedBrowserExited = 0;
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    // Off any plausible desktop; WGC captures regardless of visibility as
    // long as the window is shown and unminimized.
    private const int OffscreenOrigin = -4000;
    private const uint TIMER_CAPTURE_WATCHDOG = 1;
    private const uint WatchdogIntervalMs = 5000;
    private const uint FastWatchdogIntervalMs = 1000;
    // Bounds the wait for the touch-emulation completion that starts the navigation.
    private const uint TIMER_NAVIGATE_FALLBACK = 2;
    private const uint NavigateFallbackMs = 2000;
    // Above compositor scheduling jitter, below what a viewer reads as a
    // time-jump. WGC is change-driven, so slow-changing content gaps
    // legitimately on every content update; hits are sparse-logged.
    private const long CaptureGapLogThresholdMs = 100;

    private static readonly ConcurrentDictionary<int, StreamPanelHost> _instances = new();
    private static int _nextInstanceId;
    private readonly int _instanceId;

    public IntPtr Hwnd { get; private set; }
    public string SessionId { get; }

    /// <summary>Fires once (message-loop thread) when the host dies for any
    /// reason other than an explicit Dispose: ingest fault, engine init
    /// failure, external HWND destruction. The manager drops its entry; the
    /// next reconcile respawns while the session stays desired.</summary>
    public Action? Faulted;

    /// <summary>Fires once (message-loop thread) after the host disposed itself because the WGC capture stayed dead through every
    /// in-place recovery. The manager rebuilds it after a backoff; unlike <see cref="Faulted"/> it is not an immediate respawn.</summary>
    public Action? RebuildRequested;
    public Action? SoftwareRebuildRequested;

    /// <summary>Fires once (message-loop thread) after the host disposed itself because the WebView2 browser process exited.</summary>
    public Action? BrowserExited;
    private volatile bool _browserExited;

    /// <summary>Fires once (message-loop thread) when frames have flowed past the session's initial frame, ending any rebuild backoff.</summary>
    public Action? Recovered;

    private readonly StreamAssignment _assignment;
    private readonly string _serviceOrigin;
    private readonly string _pairedToken;
    private readonly string _navigationUrl;
    private readonly int _pixelWidth;
    private readonly int _pixelHeight;

    private IntPtr _env;
    private IntPtr _envOptions;
    private IntPtr _envCreatedHandler;
    private IntPtr _ctrlCreatedHandler;
    private IntPtr _navCompletedHandler;
    private IntPtr _controller;
    private IntPtr _coreWebView2;
    private long _navCompletedToken;
    private IntPtr _webMessageHandler;
    private long _webMessageToken;
    private string? _lastHeartbeat;
    private long _lastHeartbeatTicks;
    private long _lastHeartbeatLogTicks;
    private long _lastStallLogTicks;
    private string? _lastHeartbeatState;
    private long _lastRafCount = -1;
    private IntPtr _processFailedHandler;
    private long _processFailedToken;
    private int _processFailedLogged;
    private int _flowingTicks;
    private bool _recoveredReported;

    private D3DDevice? _d3d;
    private WgcCapture? _capture;
    private IFrameSink? _encoder;
    private IngestClient? _ingest;
    private SharedFrameLink? _shared;
    private Thread? _pumpThread;
    private volatile bool _pumpStop;
    private long _framesPumped;
    private long _framesAtLastTick;
    private long _lastFrameArrivalTicks;
    private long _lastFrameTimeTicks;
    private long _captureGapCount;
    private int _restartRequested;
    private readonly StreamWatchdogPolicy _watchdog;
    // One handler per probe: an abandoned probe's late completion finds no host.
    private IntPtr _probeHandler;
    private int _probeFrameExpected;
    private bool _engineStarted;
    private bool _navigateStarted;
    private bool _disposed;

    // Touch from the glass: the client queues from its worker, the message loop drains into CDP.
    // A null entry marks a dropped stream, which cancels whatever is still down.
    private PanelTouchClient? _touch;
    private readonly ConcurrentQueue<PanelTouchContact?> _touchQueue = new();
    private readonly CdpTouchState _touchState = new();
    private int _touchPosted;
    private IntPtr _cdpEmulationHandler;
    private IntPtr _cdpTouchHandler;
    private int _emulationFailureLogged;
    private int _touchFailureLogged;
    private bool _taskbarTabPending;
    private readonly bool _software;
    private bool _fastTimer;
    private readonly bool _deleteStaleSoftwareProfile;

    public StreamPanelHost(StreamAssignment assignment, string serviceOrigin, string pairedToken, bool software, bool deleteStaleSoftwareProfile)
    {
        _software = software;
        _watchdog = new StreamWatchdogPolicy(fastDetect: !software);
        _deleteStaleSoftwareProfile = deleteStaleSoftwareProfile;
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _assignment = assignment;
        SessionId = assignment.SessionId;
        _serviceOrigin = serviceOrigin;
        _pairedToken = pairedToken;
        // streamFps lets the page cap its shader draws near what the capture keeps.
        _navigationUrl = $"{serviceOrigin}/panel/{Uri.EscapeDataString(assignment.PanelDeviceId)}?token={Uri.EscapeDataString(pairedToken)}&streamFps={Math.Clamp(assignment.Fps, 1, 240)}";
        // NV12 and the encoder require even dimensions; window, frame pool,
        // and both MFT types must all use this exact value or the output is
        // stride garbage.
        _pixelWidth = MakeEven((int)Math.Round(assignment.CssWidth * assignment.Dpr));
        _pixelHeight = MakeEven((int)Math.Round(assignment.CssHeight * assignment.Dpr));

        try
        {
            Hwnd = Win32Window.Create(
                WindowClassName,
                $"Nexus Stream {assignment.PanelDeviceId}",
                Native.WS_POPUP,
                (uint)Native.WS_EX_TOOLWINDOW,
                OffscreenOrigin, OffscreenOrigin, _pixelWidth, _pixelHeight,
                this);
            Log.Info($"stream-host {SessionId}: ctor {_pixelWidth}x{_pixelHeight}@{assignment.Fps} hwnd=0x{Hwnd:X}");
            Native.ShowWindow(Hwnd, Native.SW_SHOWNOACTIVATE);
            StartWebView2Init();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INIT_CONTROLLER:
                InitController();
                return IntPtr.Zero;

            case WM_HOST_FAULTED:
                if (!_disposed)
                {
                    var callback = _browserExited ? BrowserExited : Faulted;
                    Dispose();
                    try { callback?.Invoke(); } catch { }
                }
                return IntPtr.Zero;

            case WM_TOUCH_CONTACTS:
                DrainTouch();
                return IntPtr.Zero;

            case Native.WM_TIMER:
                if (wParam == (nint)TIMER_CAPTURE_WATCHDOG)
                {
                    OnWatchdogTick();
                    return IntPtr.Zero;
                }
                if (wParam == (nint)TIMER_NAVIGATE_FALLBACK)
                {
                    Log.Warn($"stream-host {SessionId}: {EmulationMethod} did not complete in {NavigateFallbackMs} ms, navigating");
                    Navigate();
                    return IntPtr.Zero;
                }
                return null;

            case Native.WM_DESTROY:
                // External HWND destruction is a fault, not a teardown: the
                // manager must drop its entry or the still-desired session is
                // never respawned and a zombie Count pins IsIdle forever.
                if (!_disposed)
                {
                    var destroyedCallback = Faulted;
                    Dispose();
                    try { destroyedCallback?.Invoke(); } catch { }
                }
                return IntPtr.Zero;
        }
        return null;
    }

    // ===================== WebView2 init =====================

    private void StartWebView2Init()
    {
        // Browser arguments only apply when the browser process launches for a user-data folder, so software
        // compositing gets its own folder; a still-running GPU-mode browser cannot swallow the new arguments.
        var software = _software;
        var userDataDir = UserDataPaths.Profile(software ? "StreamWebView2-sw" : "StreamWebView2", ProfileMigration.UiState);
        var browserArgs = software
            ? Wv2EnvironmentOptions.EffectiveBrowserArguments + " --disable-gpu"
            : Wv2EnvironmentOptions.EffectiveBrowserArguments;
        try { Directory.CreateDirectory(userDataDir); } catch { }
        if (!software && _deleteStaleSoftwareProfile) DeleteStaleSoftwareProfile(userDataDir);

        Log.Info($"stream-host {SessionId}: env create mode={(software ? "software" : "gpu")} args='{browserArgs}' userDataDir='{RedactLocalAppData(userDataDir)}'");
        _envOptions = Wv2EnvironmentOptions.Create(browserArgs);
        _envCreatedHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);
        fixed (char* udf = userDataDir)
        {
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(
                null, udf, _envOptions, _envCreatedHandler);
            if (WebView2Native.Failed(hr))
            {
                Log.Error($"stream-host {SessionId}: env init failed hr=0x{hr:X8}");
                WebView2RuntimeInstaller.OnEnvInitFailed("stream-host", hr, userInitiated: false);
                PostFault();
            }
        }
    }

    // Off the message loop: a profile folder can hold many files.
    private static void DeleteStaleSoftwareProfile(string gpuUserDataDir)
    {
        var parent = Path.GetDirectoryName(gpuUserDataDir);
        if (parent is null) return;
        var swDir = Path.Combine(parent, "StreamWebView2-sw");
        if (string.Equals(swDir, gpuUserDataDir, StringComparison.OrdinalIgnoreCase)) return;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(swDir)) return;
                Directory.Delete(swDir, recursive: true);
                Log.Info($"stream-host: removed stale software profile '{RedactLocalAppData(swDir)}'");
            }
            catch { }
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnvCreatedStatic(IntPtr self, int errorCode, IntPtr env)
    {
        FindByEnvHandler(self)?.OnEnvCreated(errorCode, env);
        return WebView2Native.S_OK;
    }

    private void OnEnvCreated(int errorCode, IntPtr env)
    {
        if (WebView2Native.Failed(errorCode) || env == IntPtr.Zero)
        {
            Log.Error($"stream-host {SessionId}: env created err=0x{errorCode:X8}");
            PostFault();
            return;
        }
        Wv2.AddRef(env);
        _env = env;
        Native.PostMessageW(Hwnd, WM_INIT_CONTROLLER, IntPtr.Zero, IntPtr.Zero);
    }

    private void InitController()
    {
        if (_disposed || _env == IntPtr.Zero) return;
        _ctrlCreatedHandler = WebView2Callbacks.CreateControllerCreatedHandler(&OnControllerCreatedStatic);
        var hr = Wv2.Env_CreateCoreWebView2Controller(_env, Hwnd, _ctrlCreatedHandler);
        if (WebView2Native.Failed(hr))
        {
            Log.Error($"stream-host {SessionId}: controller create hr=0x{hr:X8}");
            PostFault();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnControllerCreatedStatic(IntPtr self, int errorCode, IntPtr controller)
    {
        FindByCtrlHandler(self)?.OnControllerCreated(errorCode, controller);
        return WebView2Native.S_OK;
    }

    private void OnControllerCreated(int errorCode, IntPtr controller)
    {
        if (_disposed) return;
        if (WebView2Native.Failed(errorCode) || controller == IntPtr.Zero)
        {
            Log.Error($"stream-host {SessionId}: controller created err=0x{errorCode:X8}");
            PostFault();
            return;
        }
        Wv2.AddRef(controller);
        _controller = controller;

        // The panel must rasterize at the device's dpr regardless of which
        // monitor the OS thinks the off-screen window is near.
        var controller3 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller3);
        if (controller3 != IntPtr.Zero)
        {
            Wv2.Ctrl3_put_RasterizationScale(controller3, _assignment.Dpr);
            Wv2.Ctrl3_put_ShouldDetectMonitorScaleChanges(controller3, false);
            Wv2.Release(controller3);
        }

        Native.GetClientRect(Hwnd, out var rc);
        Wv2.Ctrl_put_Bounds(_controller, rc);
        Wv2.Ctrl_put_IsVisible(_controller, true);

        if (WebView2Native.Failed(Wv2.Ctrl_get_CoreWebView2(_controller, out _coreWebView2)) || _coreWebView2 == IntPtr.Zero)
        {
            Log.Error($"stream-host {SessionId}: get_CoreWebView2 failed");
            PostFault();
            return;
        }

        if (WebView2Native.Succeeded(Wv2.Wv2_get_Settings(_coreWebView2, out var settings)) && settings != IntPtr.Zero)
        {
            Wv2.Settings_put_AreDefaultContextMenusEnabled(settings, false);
            Wv2.Settings_put_AreDevToolsEnabled(settings, false);
            Wv2.Settings_put_IsStatusBarEnabled(settings, false);
            Wv2.Settings_put_IsZoomControlEnabled(settings, false);
            Wv2.Release(settings);
        }

        _navCompletedHandler = WebView2Callbacks.CreateNavigationCompletedHandler(&OnNavigationCompletedStatic);
        Wv2.Wv2_add_NavigationCompleted(_coreWebView2, _navCompletedHandler, out _navCompletedToken);

        _processFailedHandler = WebView2Callbacks.CreateProcessFailedHandler(&OnProcessFailedStatic);
        Wv2.Wv2_add_ProcessFailed(_coreWebView2, _processFailedHandler, out _processFailedToken);

        // Page liveness: the heartbeat says whether the page is alive but not painting, throttled or dead.
        _webMessageHandler = WebView2Callbacks.CreateWebMessageReceivedHandler(&OnWebMessageReceivedStatic);
        Wv2.Wv2_add_WebMessageReceived(_coreWebView2, _webMessageHandler, out _webMessageToken);
        Wv2.Wv2_AddScriptToExecuteOnDocumentCreated(_coreWebView2, HeartbeatScript);

        // Touch emulation settles before the page loads, so it boots reporting touch support as it
        // would on a touchscreen. Its completion navigates, failed or not; a completion that never
        // arrives is bounded by the fallback timer, so CDP never keeps the page from loading.
        if (_assignment.Touch)
        {
            _cdpEmulationHandler = WebView2Callbacks.CreateCallDevToolsProtocolMethodCompletedHandler(&OnCdpCompletedStatic);
            _cdpTouchHandler = WebView2Callbacks.CreateCallDevToolsProtocolMethodCompletedHandler(&OnCdpCompletedStatic);
            if (CallCdp(EmulationMethod, "{\"enabled\":true,\"maxTouchPoints\":10}", _cdpEmulationHandler, ref _emulationFailureLogged)
                && Native.SetTimer(Hwnd, (nuint)TIMER_NAVIGATE_FALLBACK, NavigateFallbackMs, IntPtr.Zero) != 0)
                return;
        }
        Navigate();
    }

    private void Navigate()
    {
        if (_disposed || _coreWebView2 == IntPtr.Zero || _navigateStarted) return;
        _navigateStarted = true;
        Native.KillTimer(Hwnd, (nuint)TIMER_NAVIGATE_FALLBACK);
        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"stream-host {SessionId}: navigate hr=0x{hr:X8}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationCompletedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var success = true;
        if (args != IntPtr.Zero)
            Wv2.NavCompletedArgs_get_IsSuccess(args, out success);
        var status = -1;
        if (args != IntPtr.Zero)
            Wv2.NavCompletedArgs_get_WebErrorStatus(args, out status);
        var host = FindByNavHandler(self);
        host?.Log_NavCompleted(success, status);
        host?.OnNavigationCompleted(success);
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnProcessFailedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var kind = -1;
        if (args != IntPtr.Zero && WebView2Native.Succeeded(Wv2.ProcessFailedArgs_get_Kind(args, out var read)))
            kind = read;
        FindByProcessFailedHandler(self)?.OnProcessFailed(kind);
        return WebView2Native.S_OK;
    }

    // Only the failure kind is read; the page is not recovered here, the capture watchdog escalates when frames stop.
    private void OnProcessFailed(int kind)
    {
        if (_disposed) return;
        var count = Interlocked.Increment(ref _processFailedLogged);
        if (count <= 5)
            Log.Warn($"stream-host {SessionId}: webview process failed kind={kind} (x{count}); {DescribeWindowState()}");
        // The browser process is gone, so nothing will present again: respawn now instead of burning watchdog cycles.
        if (kind == ProcessFailedBrowserExited)
        {
            _browserExited = true;
            PostFault();
        }
    }

    // Why a host that is up stops presenting: a hidden, minimized or DWM-cloaked window, or a controller that
    // is invisible or has collapsed bounds, all starve the compositor without any error.
    private string DescribeWindowState()
    {
        try
        {
            var hwnd = Hwnd;
            var cloaked = 0;
            Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, sizeof(int));
            var window = $"window visible={Native.IsWindowVisible(hwnd)} iconic={Native.IsIconic(hwnd)} cloaked={cloaked}";
            if (_controller == IntPtr.Zero) return $"{window} controller=none";
            var visible = WebView2Native.Succeeded(Wv2.Ctrl_get_IsVisible(_controller, out var v)) ? v.ToString() : "?";
            var bounds = WebView2Native.Succeeded(Wv2.Ctrl_get_Bounds(_controller, out var b)) ? $"{b.Left},{b.Top} {b.Width}x{b.Height}" : "?";
            return $"{window} controller visible={visible} bounds={bounds}; {DescribeHeartbeat()}";
        }
        catch (Exception ex)
        {
            return $"window state unavailable: {ex.GetType().Name}";
        }
    }

    // One rAF is requested per tick (not a free-running loop) so the probe itself does not keep the compositor busy.
    private const string HeartbeatScript =
        "(function(){if(window.top!==window||window.__nexusHb)return;window.__nexusHb=1;var n=0;"
        + "setInterval(function(){requestAnimationFrame(function(){n++});"
        + "try{window.chrome.webview.postMessage('__nexus_hb:'+JSON.stringify({rafCount:n,visibilityState:document.visibilityState,hidden:document.hidden,documentReadyState:document.readyState}))}catch(e){}},5000)})()";
    private const string HeartbeatPrefix = "__nexus_hb:";
    private const long HeartbeatLogIntervalMs = 30_000;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnWebMessageReceivedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            var owner = FindByWebMessageHandler(self);
            if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
            var json = Wv2.WebMsgArgs_GetJson(args);
            // The page posts the heartbeat as a JSON string literal.
            if (json is null || !json.StartsWith("\"" + HeartbeatPrefix, StringComparison.Ordinal)) return WebView2Native.S_OK;
            owner.OnHeartbeat(json.Substring(HeartbeatPrefix.Length + 1, json.Length - HeartbeatPrefix.Length - 2).Replace("\\\"", "\""));
        }
        catch { }
        return WebView2Native.S_OK;
    }

    // The profile path carries the Windows user name; log it relative to the local app data root.
    private static string RedactLocalAppData(string path)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return !string.IsNullOrEmpty(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? "%LOCALAPPDATA%" + path.Substring(root.Length)
            : path;
    }

    private void OnHeartbeat(string payload)
    {
        if (_disposed) return;
        var now = Stopwatch.GetTimestamp();
        _lastHeartbeat = payload;
        _lastHeartbeatTicks = now;
        // The payload is {"rafCount":N,<state fields>}: log when the state flips, and (rate-limited) while rAF stops advancing.
        var comma = payload.IndexOf(',');
        var state = comma < 0 ? payload : payload.Substring(comma);
        long raf = -1;
        var colon = payload.IndexOf(':');
        if (colon >= 0 && comma > colon) long.TryParse(payload.AsSpan(colon + 1, comma - colon - 1), out raf);
        var stateChanged = state != _lastHeartbeatState;
        var rafStuck = raf >= 0 && raf == _lastRafCount;
        _lastHeartbeatState = state;
        _lastRafCount = raf;
        var due = _lastHeartbeatLogTicks == 0 || (now - _lastHeartbeatLogTicks) * 1000 / Stopwatch.Frequency >= HeartbeatLogIntervalMs;
        if (stateChanged || (rafStuck && due))
        {
            _lastHeartbeatLogTicks = now;
            Log.Info($"stream-host {SessionId}: page heartbeat {payload}");
        }
    }

    private string DescribeHeartbeat()
    {
        if (_lastHeartbeat is null) return "page heartbeat=none";
        var ageMs = (Stopwatch.GetTimestamp() - _lastHeartbeatTicks) * 1000 / Stopwatch.Frequency;
        return $"page heartbeat={_lastHeartbeat} age={ageMs}ms";
    }

    private void Log_NavCompleted(bool success, int status)
    {
        if (!_disposed) Log.Info($"stream-host {SessionId}: navigation completed success={success} webErrorStatus={status}");
    }

    private void OnNavigationCompleted(bool success)
    {
        if (_disposed || _engineStarted) return;
        if (!success)
        {
            // Streaming the Chromium error page to the device helps nobody;
            // fault and let the respawn retry the navigation.
            Log.Error($"stream-host {SessionId}: navigation failed");
            PostFault();
            return;
        }
        _engineStarted = true;
        try
        {
            StartEngine();
        }
        catch (Exception ex)
        {
            Log.Error($"stream-host {SessionId}: engine start failed: {ex.GetType().Name}: {ex.Message}");
            PostFault();
            return;
        }
        if (_assignment.Touch)
            StartTouch();
    }

    // ===================== touch =====================

    private const string EmulationMethod = "Emulation.setTouchEmulationEnabled";
    private const string TouchMethod = "Input.dispatchTouchEvent";

    private void StartTouch()
    {
        _touch = new PanelTouchClient(_serviceOrigin, SessionId, _pairedToken, c => QueueTouch(c), () => QueueTouch(null));
        Log.Info($"stream-host {SessionId}: touch enabled");
    }

    // Worker thread: one posted message drains everything queued before it runs.
    private void QueueTouch(PanelTouchContact? contact)
    {
        _touchQueue.Enqueue(contact);
        if (Interlocked.Exchange(ref _touchPosted, 1) == 0
            && !Native.PostMessageW(Hwnd, WM_TOUCH_CONTACTS, IntPtr.Zero, IntPtr.Zero))
            Volatile.Write(ref _touchPosted, 0);
    }

    private void DrainTouch()
    {
        Volatile.Write(ref _touchPosted, 0);
        while (_touchQueue.TryDequeue(out var contact))
        {
            if (_disposed || _coreWebView2 == IntPtr.Zero) continue;
            var json = contact is { } c ? _touchState.Apply(c, _assignment.CssWidth, _assignment.CssHeight) : _touchState.ReleaseAll();
            if (json is not null)
                CallCdp(TouchMethod, json, _cdpTouchHandler, ref _touchFailureLogged);
        }
    }

    // Each method logs its first failure once per host, so a broken path does not log per contact.
    // False when the call was refused, in which case its completion handler never runs.
    private bool CallCdp(string method, string json, IntPtr handler, ref int failureLogged)
    {
        var hr = Wv2.Wv2_CallDevToolsProtocolMethod(_coreWebView2, method, json, handler);
        if (WebView2Native.Succeeded(hr)) return true;
        if (Interlocked.Exchange(ref failureLogged, 1) == 0)
            Log.Warn($"stream-host {SessionId}: {method} hr=0x{hr:X8}");
        return false;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnCdpCompletedStatic(IntPtr self, int errorCode, IntPtr resultJson)
    {
        FindByCdpHandler(self)?.OnCdpCompleted(self, errorCode, resultJson);
        return WebView2Native.S_OK;
    }

    private void OnCdpCompleted(IntPtr handler, int errorCode, IntPtr resultJson)
    {
        bool emulation = handler == _cdpEmulationHandler;
        if (WebView2Native.Failed(errorCode)
            && Interlocked.Exchange(ref (emulation ? ref _emulationFailureLogged : ref _touchFailureLogged), 1) == 0)
            Log.Warn($"stream-host {SessionId}: {(emulation ? EmulationMethod : TouchMethod)} failed err=0x{errorCode:X8} {Marshal.PtrToStringUni(resultJson)}");
        if (emulation)
            Navigate();
    }

    // ===================== capture -> encode -> ingest =====================

    private void StartEngine()
    {
        // Wire values are clamped so a service-side typo (fps 0) cannot
        // become a divide-by-zero fault-respawn loop.
        var fps = Math.Clamp(_assignment.Fps, 1, 240);
        var bitrateKbps = Math.Clamp(_assignment.BitrateKbps, 250, 50_000);
        _d3d = D3DDevice.Create();
        _capture = CreateCapture(_d3d);
        var writerNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var ingest = new IngestClient(_serviceOrigin, SessionId, _pairedToken, writerNonce);
        ingest.Faulted = PostFault;
        _ingest = ingest;
        // Raw frames go to glass that takes a framebuffer (the Kraken LCD); everything
        // else is hardware-encoded. Same Submit contract either way.
        var raw = string.Equals(_assignment.Codec, "rawBgra", StringComparison.OrdinalIgnoreCase);
        Log.Info($"stream-host {SessionId}: codec='{_assignment.Codec}' sink={(raw ? "raw" : "h264")}");
        if (raw)
        {
            RawFrameSink? sink = null;
            if (!string.IsNullOrEmpty(_assignment.FrameSection) && _assignment.FrameSectionBytes > 0)
            {
                _shared = new SharedFrameLink($"{_assignment.FrameSection}-{writerNonce}", _assignment.FrameSectionBytes, PostFault, $"stream-host {SessionId}");
            }
            Log.Info($"stream-host {SessionId}: frame handoff={(_shared is null ? "http" : "shared section")}");
            sink = new RawFrameSink(_d3d, _pixelWidth, _pixelHeight,
                (frame, key) => _ingest?.Send(frame, key, sink!.Return), SessionId, _shared);
            _encoder = sink;
        }
        else
        {
            _encoder = new MfEncoder(_d3d, _pixelWidth, _pixelHeight, fps, bitrateKbps,
                (accessUnit, idr) => _ingest?.Send(accessUnit, idr), SessionId);
        }

        _pumpStop = false;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = $"stream-pump-{SessionId}",
        };
        _pumpThread.Start();

        _fastTimer = _watchdog.FastDetectWindow && !PanelMonitorGuard.IsSessionLocked;
        Native.SetTimer(Hwnd, (nuint)TIMER_CAPTURE_WATCHDOG, _fastTimer ? FastWatchdogIntervalMs : WatchdogIntervalMs, IntPtr.Zero);
        Log.Info($"stream-host {SessionId}: engine started");
    }

    private WgcCapture CreateCapture(D3DDevice d3d)
    {
        try
        {
            return new WgcCapture(Hwnd, d3d, _pixelWidth, _pixelHeight);
        }
        catch (CaptureItemException ex) when (ex.HResult == E_INVALIDARG)
        {
            Log.Warn($"stream-host {SessionId}: CreateForWindow refused the tool window; capturing it as a plain window");
            CaptureWindowStyle.ClearToolWindow(Hwnd);
            var capture = new WgcCapture(Hwnd, d3d, _pixelWidth, _pixelHeight);
            // The shell adds the taskbar button asynchronously after the show;
            // the next watchdog tick deletes it.
            _taskbarTabPending = true;
            return capture;
        }
    }

    private void PumpLoop()
    {
        // The poll sleeps in PumpFrames run at Windows' default 15.6 ms timer granularity
        // otherwise, which with a two-buffer capture pool drops one 60 Hz frame in five.
        TimeBeginPeriod(1);
        try
        {
            PumpFrames();
        }
        finally
        {
            TimeEndPeriod(1);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint ms);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint ms);

    private void PumpFrames()
    {
        var capture = _capture;
        var encoder = _encoder;
        if (capture is null || encoder is null) return;
        // Decimate capture (compositor rate, ~60Hz) down to the assignment
        // fps. The assignment runs below the device's display rate on
        // purpose: with production under consumption the delivery chain
        // (socket, adb window, device fifo) stays empty and frames arrive
        // writer-paced; at parity one transient leaves those buffers
        // standing full and the render-on-arrival player turns the
        // congestion into visible time snaps.
        var targetFps = Math.Clamp(_assignment.Fps, 1, 240);
        var submitStartTicks = 0L;
        var submitted = 0L;
        while (!_pumpStop)
        {
            // The pump is the only thread that touches the WGC objects, so
            // the watchdog's restart request executes here rather than
            // racing TryGetNextFrame with a released pool.
            if (Interlocked.Exchange(ref _restartRequested, 0) == 1)
            {
                try
                {
                    capture.Restart();
                }
                catch (Exception ex)
                {
                    Log.Error($"stream-host {SessionId}: WGC restart failed: {ex.Message}");
                    PostFault();
                    return;
                }
            }
            IntPtr texture;
            bool got;
            long frameTimeTicks;
            try
            {
                got = capture.TryGetNextFrame(out texture, out frameTimeTicks);
            }
            catch (Exception ex)
            {
                Log.Error($"stream-host {SessionId}: capture poll failed: {ex.Message}");
                PostFault();
                return;
            }
            if (!got || texture == IntPtr.Zero)
            {
                // The pool is vsync-fed; a short sleep keeps poll cost nil
                // without adding a visible frame of latency.
                Thread.Sleep(2);
                continue;
            }
            // A capture gap is a hole in the content clock: the panel holds
            // the last frame for the gap, then content time snaps forward.
            // frameTs localizes it: a matching frameTs gap means the
            // compositor produced nothing (renderer/page stall); a
            // near-frame-interval frameTs delta means this thread polled late
            // and the pool absorbed it.
            var nowTicks = Stopwatch.GetTimestamp();
            // A probe-forced frame ends a silence the watchdog caused on purpose; it is not a capture gap.
            var probeFrame = Interlocked.Exchange(ref _probeFrameExpected, 0) == 1;
            if (_lastFrameArrivalTicks != 0)
            {
                var arrivalMs = (nowTicks - _lastFrameArrivalTicks) * 1000 / Stopwatch.Frequency;
                if (arrivalMs > CaptureGapLogThresholdMs && !probeFrame)
                {
                    var count = ++_captureGapCount;
                    if (count <= 3 || count % 100 == 0)
                    {
                        var frameTs = frameTimeTicks == 0 || _lastFrameTimeTicks == 0
                            ? "?"
                            : $"{(frameTimeTicks - _lastFrameTimeTicks) / 10_000}";
                        Log.Warn($"stream-host {SessionId}: capture gap arrival={arrivalMs}ms frameTs={frameTs}ms (x{count})");
                    }
                }
            }
            _lastFrameArrivalTicks = nowTicks;
            _lastFrameTimeTicks = frameTimeTicks;
            if (Interlocked.Increment(ref _framesPumped) == 1)
                Log.Info($"stream-host {SessionId}: first frame captured");
            if (submitStartTicks == 0) submitStartTicks = nowTicks;
            var allowed = 1 + (nowTicks - submitStartTicks) * targetFps / Stopwatch.Frequency;
            if (allowed - submitted > 3)
            {
                // A capture gap banked budget; spending it would burst the
                // chain at compositor rate. Re-anchor instead.
                submitStartTicks = nowTicks;
                submitted = 0;
            }
            // One frame of slack: a page drawing at the stream rate lands on the display clock's
            // grid, a little early or late, and must not lose a frame to that jitter.
            else if (submitted > allowed)
            {
                // Over the fps budget: skip this frame. The texture is
                // caller-owned and would otherwise leak.
                Wv2.Release(texture);
                continue;
            }
            submitted++;
            try
            {
                encoder.Submit(texture);
            }
            catch (Exception ex)
            {
                Log.Error($"stream-host {SessionId}: encode submit failed: {ex.Message}");
                PostFault();
                return;
            }
        }
    }

    // WGC on an off-screen window can silently stop delivering frames; recreating item+pool+session on the
    // same HWND re-hooks it. Never reload the page in place (kills capture permanently). Zero frames is also a static
    // page, so the probe forces a repaint and a restart needs that repaint to reach no WGC frame. Only when
    // those in-place restarts keep yielding nothing does the host rebuild its window and WebView2 from scratch.
    private void OnWatchdogTick()
    {
        if (_disposed || _capture is null) return;
        if (_taskbarTabPending)
        {
            _taskbarTabPending = false;
            CaptureWindowStyle.DeleteTaskbarTab(Hwnd);
        }
        var pumped = Interlocked.Read(ref _framesPumped);
        var delta = pumped - _framesAtLastTick;
        _framesAtLastTick = pumped;
        var atCeiling = _watchdog.ZeroTicks + 1 >= StreamWatchdogPolicy.CeilingZeroTicks;
        var action = _watchdog.Tick(delta, PanelMonitorGuard.IsSessionLocked);
        if (delta > 0)
        {
            if (++_flowingTicks >= StreamRebuildBackoff.RecoveredFlowingTicks && !_recoveredReported)
            {
                _recoveredReported = true;
                try { Recovered?.Invoke(); } catch { }
            }
        }
        else
        {
            _flowingTicks = 0;
            // First silent tick of a stall: state the page's last heartbeat now rather than waiting for a restart.
            var stallNow = Stopwatch.GetTimestamp();
            if (_lastStallLogTicks == 0 || (stallNow - _lastStallLogTicks) * 1000 / Stopwatch.Frequency >= HeartbeatLogIntervalMs)
            {
                _lastStallLogTicks = stallNow;
                Log.Warn($"stream-host {SessionId}: no frames this tick; {DescribeWindowState()}");
            }
        }
        var fast = _watchdog.FastDetectWindow;
        if (fast != _fastTimer)
        {
            _fastTimer = fast;
            Native.SetTimer(Hwnd, (nuint)TIMER_CAPTURE_WATCHDOG, fast ? FastWatchdogIntervalMs : WatchdogIntervalMs, IntPtr.Zero);
        }
        // Frames, a timeout or a restart ended the probe; its completion, if it ever comes, is dropped.
        if (!_watchdog.ProbeInFlight)
        {
            ReleaseHandler(ref _probeHandler);
            Interlocked.Exchange(ref _probeFrameExpected, 0);
        }
        switch (action)
        {
            case WatchdogAction.Probe:
                StartProbe();
                break;
            case WatchdogAction.Restart:
                RequestRestart(atCeiling
                    ? $"no frames for {StreamWatchdogPolicy.CeilingZeroTicks} ticks (probe bound)"
                    : _watchdog.RepaintRestartsExhausted
                        ? "forced repaint produced no WGC frame; repaint restarts pause until frames return"
                        : "forced repaint produced no WGC frame");
                break;
            case WatchdogAction.RebuildSoftware:
                Log.Warn($"stream-host {SessionId}: only the initial frame was delivered and repeated forced repaints produced no WGC frame, rebuilding in software compositing; {DescribeWindowState()}");
                var softwareCallback = SoftwareRebuildRequested;
                Dispose();
                try { softwareCallback?.Invoke(); } catch { }
                break;
            case WatchdogAction.Rebuild:
                Log.Warn($"stream-host {SessionId}: {StreamWatchdogPolicy.MaxStuckRestarts} WGC recreates yielded no frames, rebuilding the stream host; {DescribeWindowState()}");
                var callback = RebuildRequested;
                Dispose();
                try { callback?.Invoke(); } catch { }
                break;
        }
    }

    private void RequestRestart(string reason)
    {
        Log.Warn($"stream-host {SessionId}: {reason}, recreating WGC session; {DescribeWindowState()}");
        Interlocked.Exchange(ref _restartRequested, 1);
    }

    // CapturePreview and CDP Page.captureScreenshot take a DisplayRequired power request in the browser
    // process, resetting the Windows display-off and screensaver idle timers; ExecuteScript does not.
    // Toggling a tiny near-transparent element forces one composited frame without a visible change.
    private const string ProbeScript =
        "(function(){var e=document.getElementById('__nexus_wgc_probe');if(!e){e=document.createElement('div');e.id='__nexus_wgc_probe';e.style.cssText='position:fixed;left:0;top:0;width:1px;height:1px;pointer-events:none;z-index:2147483647;background:rgba(0,0,0,0.01)';document.documentElement.appendChild(e);}e.style.opacity=e.style.opacity==='0.5'?'1':'0.5';return true})()";

    private void StartProbe()
    {
        ReleaseHandler(ref _probeHandler);
        if (_coreWebView2 == IntPtr.Zero)
        {
            _watchdog.OnProbeFailed();
            return;
        }
        _probeHandler = WebView2Callbacks.CreateExecuteScriptCompletedHandler(&OnProbeCompletedStatic);
        Interlocked.Exchange(ref _probeFrameExpected, 1);
        if (WebView2Native.Failed(Wv2.Wv2_ExecuteScript(_coreWebView2, ProbeScript, _probeHandler)))
        {
            ReleaseHandler(ref _probeHandler);
            Interlocked.Exchange(ref _probeFrameExpected, 0);
            _watchdog.OnProbeFailed();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnProbeCompletedStatic(IntPtr self, int errorCode, IntPtr resultJson)
    {
        // resultJson is caller-owned; borrow, don't free.
        FindByProbeHandler(self)?.OnProbeCompleted(errorCode, resultJson == IntPtr.Zero ? null : Marshal.PtrToStringUni(resultJson));
        return WebView2Native.S_OK;
    }

    private void OnProbeCompleted(int errorCode, string? resultJson)
    {
        if (_disposed) return;
        if (WebView2Native.Failed(errorCode) || resultJson != "true")
            _watchdog.OnProbeFailed();
        else
            _watchdog.OnProbe();
    }

    private void PostFault()
    {
        if (_disposed) return;
        Native.PostMessageW(Hwnd, WM_HOST_FAULTED, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.TryRemove(_instanceId, out _);

        try { Native.KillTimer(Hwnd, (nuint)TIMER_CAPTURE_WATCHDOG); } catch { }
        try { Native.KillTimer(Hwnd, (nuint)TIMER_NAVIGATE_FALLBACK); } catch { }

        _pumpStop = true;
        var pumpExited = true;
        if (_pumpThread is not null && Thread.CurrentThread != _pumpThread)
            pumpExited = _pumpThread.Join(2000);
        _pumpThread = null;

        _ingest?.Dispose();
        _ingest = null;
        _touch?.Dispose();
        _touch = null;
        if (!pumpExited)
        {
            // The pump may still be inside a wedged TryGetNextFrame/Submit on
            // these objects; leaking them beats releasing under a live call.
            Log.Warn($"stream-host {SessionId}: pump did not exit; leaking capture/encoder refs");
            _encoder = null;
            _capture = null;
            _d3d = null;
        }
        else
        {
            _encoder?.Dispose();
            _encoder = null;
            _shared?.Dispose();
            _shared = null;
            _capture?.Dispose();
            _capture = null;
            _d3d?.Dispose();
            _d3d = null;
        }

        if (_coreWebView2 != IntPtr.Zero)
        {
            if (_navCompletedHandler != IntPtr.Zero)
                Wv2.Wv2_remove_NavigationCompleted(_coreWebView2, _navCompletedToken);
            if (_processFailedHandler != IntPtr.Zero)
                Wv2.Wv2_remove_ProcessFailed(_coreWebView2, _processFailedToken);
            if (_webMessageHandler != IntPtr.Zero)
                Wv2.Wv2_remove_WebMessageReceived(_coreWebView2, _webMessageToken);
            Wv2.Release(_coreWebView2);
            _coreWebView2 = IntPtr.Zero;
        }
        if (_controller != IntPtr.Zero)
        {
            Wv2.Ctrl_Close(_controller);
            Wv2.Release(_controller);
            _controller = IntPtr.Zero;
        }
        if (_env != IntPtr.Zero)
        {
            Wv2.Release(_env);
            _env = IntPtr.Zero;
        }
        ReleaseHandler(ref _envCreatedHandler);
        ReleaseHandler(ref _ctrlCreatedHandler);
        ReleaseHandler(ref _navCompletedHandler);
        ReleaseHandler(ref _processFailedHandler);
        ReleaseHandler(ref _webMessageHandler);
        ReleaseHandler(ref _probeHandler);
        ReleaseHandler(ref _cdpEmulationHandler);
        ReleaseHandler(ref _cdpTouchHandler);
        if (_envOptions != IntPtr.Zero)
        {
            Wv2.Release(_envOptions);
            _envOptions = IntPtr.Zero;
        }

        if (Hwnd != IntPtr.Zero)
        {
            var hwnd = Hwnd;
            Hwnd = IntPtr.Zero;
            Native.DestroyWindow(hwnd);
        }
        Log.Info($"stream-host {SessionId}: disposed");
    }

    private static void ReleaseHandler(ref IntPtr handler)
    {
        if (handler == IntPtr.Zero) return;
        Wv2.Release(handler);
        handler = IntPtr.Zero;
    }

    private static int MakeEven(int value) => Math.Max(2, value & ~1);

    private static StreamPanelHost? FindByEnvHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._envCreatedHandler == h) return kv.Value;
        return null;
    }

    private static StreamPanelHost? FindByCtrlHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._ctrlCreatedHandler == h) return kv.Value;
        return null;
    }

    private static StreamPanelHost? FindByProbeHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._probeHandler == h) return kv.Value;
        return null;
    }

    private static StreamPanelHost? FindByProcessFailedHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._processFailedHandler == h) return kv.Value;
        return null;
    }

    private static StreamPanelHost? FindByWebMessageHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._webMessageHandler == h) return kv.Value;
        return null;
    }

    private static StreamPanelHost? FindByNavHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._navCompletedHandler == h) return kv.Value;
        return null;
    }

    private static StreamPanelHost? FindByCdpHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._cdpEmulationHandler == h || kv.Value._cdpTouchHandler == h) return kv.Value;
        return null;
    }
}
