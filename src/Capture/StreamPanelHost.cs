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
/// the occlusion-disable browser argument never touches the dashboard /
/// kiosk / overlay surfaces sharing DesktopWebView2.
/// </summary>
internal sealed unsafe class StreamPanelHost : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.StreamHost";
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 7;
    private const uint WM_HOST_FAULTED = Native.WM_USER + 8;
    // Off any plausible desktop; WGC captures regardless of visibility as
    // long as the window is shown and unminimized.
    private const int OffscreenOrigin = -4000;
    private const uint TIMER_CAPTURE_WATCHDOG = 1;
    private const uint WatchdogIntervalMs = 5000;
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

    private D3DDevice? _d3d;
    private WgcCapture? _capture;
    private IFrameSink? _encoder;
    private IngestClient? _ingest;
    private Thread? _pumpThread;
    private volatile bool _pumpStop;
    private long _framesPumped;
    private long _framesAtLastTick;
    private long _lastFrameArrivalTicks;
    private long _lastFrameTimeTicks;
    private long _captureGapCount;
    private int _zeroFrameTicks;
    private int _restartRequested;
    private bool _restartedThisEpisode;
    private bool _engineStarted;
    private bool _disposed;

    public StreamPanelHost(StreamAssignment assignment, string serviceOrigin, string pairedToken)
    {
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _assignment = assignment;
        SessionId = assignment.SessionId;
        _serviceOrigin = serviceOrigin;
        _pairedToken = pairedToken;
        _navigationUrl = $"{serviceOrigin}/panel/{Uri.EscapeDataString(assignment.PanelDeviceId)}?token={Uri.EscapeDataString(pairedToken)}";
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
                    var callback = Faulted;
                    Dispose();
                    try { callback?.Invoke(); } catch { }
                }
                return IntPtr.Zero;

            case Native.WM_TIMER:
                if (wParam == (nint)TIMER_CAPTURE_WATCHDOG)
                {
                    OnWatchdogTick();
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
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nexus", "StreamWebView2");
        try { Directory.CreateDirectory(userDataDir); } catch { }

        _envOptions = Wv2EnvironmentOptions.Create();
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

        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"stream-host {SessionId}: navigate hr=0x{hr:X8}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationCompletedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var success = true;
        if (args != IntPtr.Zero)
            Wv2.NavCompletedArgs_get_IsSuccess(args, out success);
        FindByNavHandler(self)?.OnNavigationCompleted(success);
        return WebView2Native.S_OK;
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
        }
    }

    // ===================== capture -> encode -> ingest =====================

    private void StartEngine()
    {
        // Wire values are clamped so a service-side typo (fps 0) cannot
        // become a divide-by-zero fault-respawn loop.
        var fps = Math.Clamp(_assignment.Fps, 1, 240);
        var bitrateKbps = Math.Clamp(_assignment.BitrateKbps, 250, 50_000);
        _d3d = D3DDevice.Create();
        _capture = new WgcCapture(Hwnd, _d3d, _pixelWidth, _pixelHeight);
        var ingest = new IngestClient(_serviceOrigin, SessionId, _pairedToken);
        ingest.Faulted = PostFault;
        _ingest = ingest;
        // Raw frames go to glass that takes a framebuffer (the Kraken LCD); everything
        // else is hardware-encoded. Same Submit contract either way.
        var raw = string.Equals(_assignment.Codec, "rawBgra", StringComparison.OrdinalIgnoreCase);
        Log.Info($"stream-host {SessionId}: codec='{_assignment.Codec}' sink={(raw ? "raw" : "h264")}");
        _encoder = raw
            ? new RawFrameSink(_d3d, _pixelWidth, _pixelHeight,
                (frame, key) => _ingest?.Send(frame, key), SessionId)
            : new MfEncoder(_d3d, _pixelWidth, _pixelHeight, fps, bitrateKbps,
                (accessUnit, idr) => _ingest?.Send(accessUnit, idr), SessionId);

        _pumpStop = false;
        _pumpThread = new Thread(PumpLoop)
        {
            IsBackground = true,
            Name = $"stream-pump-{SessionId}",
        };
        _pumpThread.Start();

        Native.SetTimer(Hwnd, (nuint)TIMER_CAPTURE_WATCHDOG, WatchdogIntervalMs, IntPtr.Zero);
        Log.Info($"stream-host {SessionId}: engine started");
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
            if (_lastFrameArrivalTicks != 0)
            {
                var arrivalMs = (nowTicks - _lastFrameArrivalTicks) * 1000 / Stopwatch.Frequency;
                if (arrivalMs > CaptureGapLogThresholdMs)
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
            Interlocked.Increment(ref _framesPumped);
            if (submitStartTicks == 0) submitStartTicks = nowTicks;
            var allowed = 1 + (nowTicks - submitStartTicks) * targetFps / Stopwatch.Frequency;
            if (allowed - submitted > 3)
            {
                // A capture gap banked budget; spending it would burst the
                // chain at compositor rate. Re-anchor instead.
                submitStartTicks = nowTicks;
                submitted = 0;
            }
            else if (submitted >= allowed)
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

    // Bench-proven staleness fix: WGC on an off-screen window silently stops
    // delivering frames after minutes; recreating item+pool+session on the
    // same HWND re-hooks in under a second. Never reload the page (kills
    // capture permanently). Zero frames is also what a fully static page
    // produces, and the two are indistinguishable here, so each zero-frame
    // episode gets exactly one restart; frames flowing again re-arms it.
    private void OnWatchdogTick()
    {
        if (_disposed || _capture is null) return;
        var pumped = Interlocked.Read(ref _framesPumped);
        var delta = pumped - _framesAtLastTick;
        _framesAtLastTick = pumped;
        if (delta > 0)
        {
            _zeroFrameTicks = 0;
            _restartedThisEpisode = false;
            return;
        }
        _zeroFrameTicks++;
        if (_zeroFrameTicks < 2 || _restartedThisEpisode) return;
        _zeroFrameTicks = 0;
        _restartedThisEpisode = true;
        Log.Warn($"stream-host {SessionId}: no frames for two ticks, recreating WGC session");
        Interlocked.Exchange(ref _restartRequested, 1);
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

        _pumpStop = true;
        var pumpExited = true;
        if (_pumpThread is not null && Thread.CurrentThread != _pumpThread)
            pumpExited = _pumpThread.Join(2000);
        _pumpThread = null;

        _ingest?.Dispose();
        _ingest = null;
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
            _capture?.Dispose();
            _capture = null;
            _d3d?.Dispose();
            _d3d = null;
        }

        if (_coreWebView2 != IntPtr.Zero)
        {
            if (_navCompletedHandler != IntPtr.Zero)
                Wv2.Wv2_remove_NavigationCompleted(_coreWebView2, _navCompletedToken);
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

    private static StreamPanelHost? FindByNavHandler(IntPtr h)
    {
        foreach (var kv in _instances)
            if (kv.Value._navCompletedHandler == h) return kv.Value;
        return null;
    }
}
