using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Overlay.Capture;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

internal static class Program
{
    // Per-session singleton. Global\ would force one overlay process across
    // ALL sessions, which breaks the cross-session schtasks spawn the service
    // uses: a transient Session 0 launch holds the Global mutex past its own
    // process death, and subsequent Session 2 spawns see firstInstance=false
    // and silently bail. Local\ keeps the singleton per logon session.
    private const string SingletonMutexName = "Local\\Nexus.Overlay.Singleton";
    private const string ServiceOrigin = "http://localhost:9400";
    private const string MarshalerClassName = "Nexus.Overlay.Marshaler";
    private const string ShowDashboardMessageName = "Nexus.Overlay.ShowDashboard";
    // Settings deep-link variant: ShowDashboard always opens/focuses at the
    // page the user last had; this one also navigates to /settings (the tray
    // "Settings" item). A dedicated message avoids cross-process string
    // marshaling - a registered message carries no payload.
    private const string ShowDashboardSettingsMessageName = "Nexus.Overlay.ShowDashboardSettings";
    private const string ShowPanelKioskMessageName = "Nexus.Overlay.ShowPanelKiosk";
    private const string HidePanelKioskMessageName = "Nexus.Overlay.HidePanelKiosk";
    private const string PrefsChangedMessageName = "Nexus.Overlay.PrefsChanged";
    private static readonly UIntPtr TIMER_PREFS_POLL = new(1);
    private static readonly UIntPtr TIMER_IDLE_EXIT = new(2);
    private const uint IdleExitDelayMs = 30_000;
    private const long StateUnreachableExitMs = 60_000;

    private static readonly List<OverlayWindow> Overlays = new();
    private static DashboardWindow? _dashboard;
    private static PanelKioskWindow? _panelKiosk;
    // Kiosks for user-promoted monitors, reconciled from /displays/assignments.
    // Distinct from _panelKiosk (the auto-detected Y70).
    private static MonitorKioskManager? _monitorKiosks;
    private static StreamHostManager? _streamHosts;
    private static uint _showDashboardMsg;
    private static uint _showDashboardSettingsMsg;
    private static uint _showPanelKioskMsg;
    private static uint _hidePanelKioskMsg;
    private static uint _prefsChangedMsg;
    private static NexusApi? _api;
    private static WebView2MemorySampler? _memSampler;
    private static string _pairedToken = "";
    private static IntPtr _marshalerHwnd;
    private static MarshalerOwner? _marshalerOwner;
    private static Win32SynchronizationContext? _syncContext;
    private static OverlayState? _state;
    private static long _stateUnreachableSinceTick;
    private static int _kioskUnconfirmedRecreates;
    private static bool _idleTimerArmed;

    private static bool ShouldShowOverlays(OverlayState s) => s.OverlayEnabled && s.Pinned > 0;

    public static void SetAllAlwaysOnTop(bool value)
    {
        foreach (var overlay in Overlays)
        {
            try { overlay.SetAlwaysOnTop(value); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Move the overlay(s) to a new monitor in-place. Triggered by the SPA's
    /// <c>setMonitor</c> webMessage, bypassing the 5 s prefs poll. Updates the
    /// poll's last-seen index so the next poll doesn't fire a redundant
    /// teardown+respawn.
    /// </summary>
    public static void MoveAllToMonitor(int value)
    {
        if (_state is not null) _state.Monitor = value;
        foreach (var overlay in Overlays)
        {
            try { overlay.MoveToMonitor(value); } catch (Exception ex) { Log.Error($"MoveToMonitor: {ex.Message}"); }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingletonMutexName, out var firstInstance);
        if (!firstInstance)
        {
            Log.Info("singleton: another instance owns the mutex; exiting");
            return 0;
        }

        // Rotate only after winning the singleton, so a losing relaunch appends
        // its exit line instead of archiving the running instance's log.
        Log.Rotate();
        Log.Info($"main start args=[{string.Join(' ', args)}]");

        // A see-through kiosk hides the taskbar on its monitor, and that
        // outlives a crash. Restoring unconditionally at startup is the only
        // recovery path the user does not have to know about.
        PanelTaskbarGuard.RestoreAll();

        // Set the AppUserModelID before any window is created so the
        // shell associates every overlay HWND with this AUMID. Required
        // for the cross-desktop pin via PinAppID later.
        VirtualDesktopPin.SetProcessAppId();

        // Per-monitor V2 DPI awareness. Fails on Win10 < 1703, which keeps
        // the older behavior.
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // WebView2 requires STA. CoInitializeEx is benign if STAThread
        // already initialized; we just ensure the apartment is what we expect.
        Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_APARTMENTTHREADED);

        _api = new NexusApi(ServiceOrigin);

        // Before the message loop exists nothing captures a sync context, so
        // blocking on the HTTP client here cannot deadlock.
        var state = BootAsync().GetAwaiter().GetResult();
        if (state is null) return 2;
        _memSampler = new WebView2MemorySampler(_api);

        // Now safe to install: WebView2 callbacks fire on this thread once
        // the message loop is pumping, and the sync context drains via the
        // registered drain message.
        _syncContext = new Win32SynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_syncContext);
        _marshalerOwner = new MarshalerOwner();
        _marshalerHwnd = Win32Window.Create(
            MarshalerClassName,
            "Nexus.Overlay.Marshaler",
            0u,
            (uint)Native.WS_EX_TOOLWINDOW,
            0, 0, 0, 0,
            _marshalerOwner);
        _syncContext.Bind(_marshalerHwnd);
        Log.Info($"marshaler hwnd=0x{_marshalerHwnd:X}");

        // Lock/unlock notifications drive the panel-monitor guard's suspend
        // state; without them the guard evicts the lock screen's per-monitor
        // windows off the panel onto the primary display. Seed only when
        // registration succeeded: a locked seed with no unlock delivery would
        // suspend the guard for the process lifetime.
        if (Native.WTSRegisterSessionNotification(_marshalerHwnd, Native.NOTIFY_FOR_THIS_SESSION))
            PanelMonitorGuard.InitializeSessionLockState();
        else
            Log.Warn($"WTSRegisterSessionNotification failed err={Marshal.GetLastWin32Error()}; lock-state suspend disabled");

        // Register the cross-process message used by the tray's "Open Nexus"
        // path. Both sender (nexus-service TrayIcon) and receiver (us) call
        // RegisterWindowMessageW with the same string and get the same ID
        // for the OS session lifetime.
        _showDashboardMsg = Native.RegisterWindowMessageW(ShowDashboardMessageName);
        Log.Info($"registered ShowDashboard msg=0x{_showDashboardMsg:X}");

        _showDashboardSettingsMsg = Native.RegisterWindowMessageW(ShowDashboardSettingsMessageName);
        Log.Info($"registered ShowDashboardSettings msg=0x{_showDashboardSettingsMsg:X}");

        _showPanelKioskMsg = Native.RegisterWindowMessageW(ShowPanelKioskMessageName);
        Log.Info($"registered ShowPanelKiosk msg=0x{_showPanelKioskMsg:X}");

        _hidePanelKioskMsg = Native.RegisterWindowMessageW(HidePanelKioskMessageName);
        Log.Info($"registered HidePanelKiosk msg=0x{_hidePanelKioskMsg:X}");

        // Register the push-notify message that the user-session helper
        // posts from `overlay.prefsChanged`. Receiving it kicks
        // the poll immediately so user-visible toggles feel instant
        // instead of waiting for the next 5 s poll tick.
        _prefsChangedMsg = Native.RegisterWindowMessageW(PrefsChangedMessageName);
        Log.Info($"registered PrefsChanged msg=0x{_prefsChangedMsg:X}");

        _monitorKiosks = new MonitorKioskManager(ServiceOrigin);
        _streamHosts = new StreamHostManager(ServiceOrigin);
        Apply(state);

        // Pin the overlay's AppID across virtual desktops. Process-level
        // pin, not per-overlay - one call covers every HWND we own. Idempotent
        // (IsAppIdPinned check inside) so repeated overlay restarts don't churn.
        VirtualDesktopPin.TryPinApp();

        Native.SetTimer(_marshalerHwnd, TIMER_PREFS_POLL, 5000, IntPtr.Zero);

        var result = MessageLoop.Run(_syncContext);

        // Cleanup: dispose kiosks, then dashboard, then per-monitor overlays.
        Native.KillTimer(_marshalerHwnd, TIMER_PREFS_POLL);
        Native.WTSUnRegisterSessionNotification(_marshalerHwnd);
        _memSampler?.Dispose();
        _memSampler = null;
        // Stream hosts first: their encoders and capture pumps must stop
        // before any shared teardown touches the windows they capture.
        _streamHosts?.CloseAll();
        _streamHosts = null;
        _monitorKiosks?.CloseAll();
        _monitorKiosks = null;
        _panelKiosk?.Dispose();
        _panelKiosk = null;
        _dashboard?.Dispose();
        _dashboard = null;
        foreach (var o in Overlays) o.Dispose();
        Overlays.Clear();
        return result;
    }

    /// <summary>
    /// Create the dashboard window for each "Open Nexus" click. The window
    /// fully tears down on close (DashboardWindow.WM_CLOSE → DestroyWindow
    /// → OnDashboardClosed clears the singleton) so every reopen does a
    /// fresh WebView2 init + navigation - important after a wwwroot
    /// redeploy. The cold start is ~1-2s.
    /// </summary>
    // deepLinkPath: when non-null, navigate to that SPA route (e.g. "/settings"
    // from the tray) on both create and focus. Null = the plain ShowDashboard
    // path: a fresh window opens at "/", but an existing one is only focused,
    // never re-navigated, so the user keeps whatever page they were on.
    private static void ShowOrCreateDashboard(string? deepLinkPath = null)
    {
        // Dashboard is appearing - cancel any pending idle exit.
        DisarmIdleExitTimer();
        if (_dashboard is null)
        {
            var url = DashboardUrl(deepLinkPath ?? "/");
            _dashboard = new DashboardWindow(url);
            Log.Info($"dashboard created path={deepLinkPath ?? "/"}");
            return;
        }
        if (deepLinkPath is not null)
        {
            _dashboard.Navigate(DashboardUrl(deepLinkPath));
        }
        _dashboard.ShowAndFocus();
        Log.Info($"dashboard focused deepLink={deepLinkPath ?? "(none)"}");
    }

    /// <summary>Dashboard URL for a path, joining the token with the separator the path itself needs; a deep link may already carry a query.</summary>
    private static string DashboardUrl(string path)
        => $"{ServiceOrigin}{path}{(path.Contains('?') ? '&' : '?')}token={Uri.EscapeDataString(_pairedToken)}";

    /// <summary>Deep-link path out of a WM_COPYDATA payload, or null when it is not ours or not a same-origin path.</summary>
    private static string? ReadDeepLinkPath(IntPtr lParam)
    {
        if (_showDashboardMsg == 0 || lParam == IntPtr.Zero) return null;
        var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
        if ((uint)cds.dwData.ToInt64() != _showDashboardMsg) return null;
        if (cds.lpData == IntPtr.Zero || cds.cbData <= 0 || cds.cbData > 1024) return null;
        var path = Marshal.PtrToStringUni(cds.lpData, cds.cbData / 2)?.TrimEnd('\0');
        // Only an absolute same-origin path; "//host" and "/\host" are
        // protocol-relative and would navigate the dashboard off-origin.
        if (string.IsNullOrEmpty(path) || path![0] != '/') return null;
        if (path.Length > 1 && (path[1] == '/' || path[1] == '\\')) return null;
        return path;
    }

    /// <summary>
    /// Invoked by DashboardWindow on WM_CLOSE before DestroyWindow tears
    /// down the HWND. Clears the singleton so the next ShowDashboard
    /// constructs a fresh window + WebView2 (picks up any newly-deployed
    /// wwwroot), and arms idle-exit if no widgets are around either.
    /// </summary>
    internal static void OnDashboardClosed()
    {
        _dashboard = null;
        MaybeArmIdleExitTimer();
    }

    /// <summary>Every surface created before the runtime arrived is an empty host HWND; rebuild them all. Called from the installer thread, marshals to the UI thread.</summary>
    internal static void OnWebView2RuntimeInstalled(bool reopenDashboard)
    {
        _syncContext?.Post(_ =>
        {
            Log.Info("webview2-runtime: installed; recreating every surface");
            if (_dashboard is not null && _dashboard.Hwnd != IntPtr.Zero)
                Native.SendMessageW(_dashboard.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            ClosePanelKiosk();
            TearDownOverlays();
            _monitorKiosks?.CloseAll();
            _streamHosts?.CloseAll();
            _state = null;
            if (reopenDashboard) ShowOrCreateDashboard();
            _ = PollAsync();
        }, null);
    }

    /// <summary>The runtime prompt held the process open; re-evaluate idle now that it is gone.</summary>
    internal static void OnWebView2RuntimePromptClosed()
        => _syncContext?.Post(_ => MaybeArmIdleExitTimer(), null);

    /// <summary>
    /// Open the panel kiosk window if a recognized HYTE touch panel is
    /// connected AND no kiosk is already up. Called at startup when
    /// <c>panel.autoLaunch</c> is on, from the <c>ShowPanelKiosk</c> cross-
    /// process message, and from the prefs poll when the toggle flips.
    /// </summary>
    private static void MaybeShowPanelKiosk()
    {
        if (_panelKiosk is not null && _panelKiosk.Hwnd != IntPtr.Zero)
        {
            Log.Info("panel kiosk already open; ignoring duplicate launch");
            return;
        }
        var target = PanelDisplay.Find();
        // Find() logs the scan result on change; no per-poll line needed here.
        if (target is null) return;
        DisarmIdleExitTimer();
        var seeThrough = IsSeeThrough(_state?.Y70Backdrop);
        var reserve = _state?.ReserveMonitor ?? true;
        var compat = _state?.Y70CompatibilityRendering ?? false;
        var url = $"{ServiceOrigin}/panel?token={Uri.EscapeDataString(_pairedToken)}{(seeThrough ? "&backdrop=desktop" : "")}";
        _panelKiosk = new PanelKioskWindow(target, url, reserve, seeThrough: seeThrough, compatibilityRendering: compat);
        var created = _panelKiosk;
        // Drop the reference on ANY teardown, including one the OS drives
        // directly (bypassing ClosePanelKiosk), so a dead kiosk never
        // wedges the next reconcile poll against a disposed reference.
        created.Destroyed = () =>
        {
            if (ReferenceEquals(_panelKiosk, created)) _panelKiosk = null;
        };
        Log.Info($"panel kiosk opened on monitor={target.Index} guard={reserve} seeThrough={seeThrough} compat={compat}");
    }

    private static bool IsSeeThrough(string? backdrop) =>
        string.Equals(backdrop, "desktop", StringComparison.Ordinal);

    private static void ClosePanelKiosk()
    {
        if (_panelKiosk is null) return;
        try { _panelKiosk.Dispose(); }
        catch (Exception ex) { Log.Error($"panel kiosk dispose: {ex.Message}"); }
        _panelKiosk = null;
        MaybeArmIdleExitTimer();
        Log.Info("panel kiosk closed");
    }

    private static bool IsIdle()
    {
        // The runtime prompt has no window of its own to keep the process alive.
        if (WebView2RuntimeInstaller.Busy) return false;
        if (Overlays.Count > 0) return false;
        if (_dashboard is not null
            && _dashboard.Hwnd != IntPtr.Zero
            && Native.IsWindowVisible(_dashboard.Hwnd))
        {
            return false;
        }
        if (_panelKiosk is not null && _panelKiosk.Hwnd != IntPtr.Zero)
        {
            return false;
        }
        if (_monitorKiosks is { Count: > 0 })
        {
            return false;
        }
        if (_streamHosts is { Count: > 0 })
        {
            return false;
        }
        return true;
    }

    private static void MaybeArmIdleExitTimer()
    {
        // SetTimer on a live id restarts its countdown, so arming on every poll
        // would hold the process open forever.
        if (_marshalerHwnd == IntPtr.Zero || _idleTimerArmed || !IsIdle()) return;
        _idleTimerArmed = true;
        Native.SetTimer(_marshalerHwnd, TIMER_IDLE_EXIT, IdleExitDelayMs, IntPtr.Zero);
        Log.Info($"idle: arming exit timer for {IdleExitDelayMs} ms");
    }

    private static void DisarmIdleExitTimer()
    {
        if (_marshalerHwnd == IntPtr.Zero) return;
        _idleTimerArmed = false;
        Native.KillTimer(_marshalerHwnd, TIMER_IDLE_EXIT);
    }

    /// <summary>
    /// Resolves the requested monitor index against the current display
    /// layout and spawns ONE overlay there. Falls back to the primary
    /// monitor when the index is the -1 sentinel or out of range.
    /// </summary>
    private static void CreateOverlay(int requestedMonitorIndex, bool alwaysOnTop)
    {
        var monitors = Monitors.Enumerate();
        Log.Info($"enumerated monitors count={monitors.Count} requested={requestedMonitorIndex}");
        if (monitors.Count == 0)
        {
            Log.Error("no monitors enumerated; cannot create overlay");
            return;
        }

        MonitorInfo target;
        if (requestedMonitorIndex >= 0 && requestedMonitorIndex < monitors.Count)
        {
            target = monitors[requestedMonitorIndex];
            Log.Info($"using requested monitor {requestedMonitorIndex}");
        }
        else
        {
            // Prefer the OS-flagged primary; fall back to index 0 if no
            // monitor advertises primary (rare, only on misconfigured GDI).
            MonitorInfo? primary = null;
            foreach (var m in monitors) if (m.Primary) { primary = m; break; }
            target = primary ?? monitors[0];
            Log.Info($"using primary fallback monitor {target.Index} (requested {requestedMonitorIndex})");
        }

        var url = $"{ServiceOrigin}/overlay?monitor={target.Index}&token={Uri.EscapeDataString(_pairedToken)}";
        var overlay = new OverlayWindow(target, url, alwaysOnTop);
        Overlays.Add(overlay);
        Log.Info($"created overlay on monitor={target.Index}");
    }

    /// <summary>
    /// Tear down every existing overlay window so a new one can be
    /// created on a different monitor without leaving stale WebView2
    /// processes behind. Called from the prefs poll when the user picks
    /// a different monitor in the popup.
    /// </summary>
    private static void TearDownOverlays()
    {
        if (Overlays.Count == 0) return;
        foreach (var o in Overlays) o.Dispose();
        Overlays.Clear();
        Log.Info("overlays torn down");
    }

    // Marshaler-window owner: handles WM_TIMER for the state poll and
    // serves as the sync-context drain destination via the registered
    // drain message (the message loop intercepts before reaching us).
    // Also receives the cross-process ShowDashboard message that the
    // service-side tray posts to wake / focus the dashboard window.
    private sealed class MarshalerOwner : IWin32WindowOwner
    {
        public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_TIMER && wParam == (IntPtr)(long)TIMER_PREFS_POLL.ToUInt64())
            {
                _ = PollAsync();
                return IntPtr.Zero;
            }
            if (msg == Native.WM_TIMER && wParam == (IntPtr)(long)TIMER_IDLE_EXIT.ToUInt64())
            {
                // Idle grace window elapsed. Re-check idle to guard against
                // a widget toggle / dashboard reopen racing the timer fire.
                Native.KillTimer(_marshalerHwnd, TIMER_IDLE_EXIT);
                _idleTimerArmed = false;
                if (IsIdle())
                {
                    Log.Info("idle exit: no widgets, dashboard hidden; quitting");
                    Native.PostQuitMessage(0);
                }
                return IntPtr.Zero;
            }
            if (msg == Native.WM_COPYDATA)
            {
                // A registered message carries no payload, so the service sends
                // an arbitrary deep-link path this way (dwData = the registered
                // ShowDashboard id, which is what identifies it as ours).
                try
                {
                    var path = ReadDeepLinkPath(lParam);
                    if (path is null) return IntPtr.Zero;
                    ShowOrCreateDashboard(path);
                    return (IntPtr)1;
                }
                catch (Exception ex) { Log.Error($"deep link: {ex.Message}"); }
                return IntPtr.Zero;
            }
            if (_showDashboardMsg != 0 && msg == _showDashboardMsg)
            {
                try { ShowOrCreateDashboard(); }
                catch (Exception ex) { Log.Error($"ShowOrCreateDashboard: {ex.Message}"); }
                return IntPtr.Zero;
            }
            if (_showDashboardSettingsMsg != 0 && msg == _showDashboardSettingsMsg)
            {
                try { ShowOrCreateDashboard("/settings"); }
                catch (Exception ex) { Log.Error($"ShowOrCreateDashboard(/settings): {ex.Message}"); }
                return IntPtr.Zero;
            }
            if (_showPanelKioskMsg != 0 && msg == _showPanelKioskMsg)
            {
                try { MaybeShowPanelKiosk(); }
                catch (Exception ex) { Log.Error($"MaybeShowPanelKiosk: {ex.Message}"); }
                return IntPtr.Zero;
            }
            if (_hidePanelKioskMsg != 0 && msg == _hidePanelKioskMsg)
            {
                try { ClosePanelKiosk(); }
                catch (Exception ex) { Log.Error($"ClosePanelKiosk: {ex.Message}"); }
                return IntPtr.Zero;
            }
            if (_prefsChangedMsg != 0 && msg == _prefsChangedMsg)
            {
                // Service signaled a settings change; repoll without
                // waiting for the next timer tick.
                _ = PollAsync();
                return IntPtr.Zero;
            }
            if (msg == Native.WM_DISPLAYCHANGE)
            {
                // Monitor hot-plug / arrangement change: re-reconcile so a
                // kiosk on an unplugged monitor closes (and a replugged
                // assigned monitor respawns) without waiting for the poll.
                _ = PollAsync();
                return IntPtr.Zero;
            }
            if (msg == Native.WM_WTSSESSION_CHANGE)
            {
                PanelMonitorGuard.OnSessionChange(wParam);
                return IntPtr.Zero;
            }
            return null;
        }
    }

    private static async System.Threading.Tasks.Task<OverlayState?> BootAsync()
    {
        var delay = 1000;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (attempt > 0) await System.Threading.Tasks.Task.Delay(delay);
            delay = Math.Min(delay * 2, 10_000);
            if (string.IsNullOrEmpty(_pairedToken)) _pairedToken = await _api!.PairAsync();
            if (string.IsNullOrEmpty(_pairedToken)) continue;
            var state = await _api!.GetStateAsync();
            if (state is null) continue;
            Log.Info($"paired ok token len={_pairedToken.Length}");
            return state;
        }
        Log.Error("service unreachable at start; exiting");
        return null;
    }

    // The timer, the PrefsChanged push and WM_DISPLAYCHANGE all fire this;
    // one poll runs at a time and re-runs once if a trigger landed mid-flight,
    // so a stale response never lands over a newer one.
    private static bool _pollInFlight;
    private static bool _pollRequeued;

    private static async System.Threading.Tasks.Task PollAsync()
    {
        if (_pollInFlight)
        {
            _pollRequeued = true;
            return;
        }
        _pollInFlight = true;
        try
        {
            do
            {
                _pollRequeued = false;
                await PollOnceAsync();
            } while (_pollRequeued);
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private static async System.Threading.Tasks.Task PollOnceAsync()
    {
        if (_api is null) return;
        try
        {
            var state = await _api.GetStateAsync();
            if (state is null)
            {
                var refreshed = await _api.PairAsync();
                if (!string.IsNullOrEmpty(refreshed))
                {
                    _pairedToken = refreshed;
                    state = await _api.GetStateAsync();
                }
            }
            if (state is null)
            {
                var now = Environment.TickCount64;
                if (_stateUnreachableSinceTick == 0) _stateUnreachableSinceTick = now;
                else if (now - _stateUnreachableSinceTick > StateUnreachableExitMs && !IsIdle()
                         && !WebView2RuntimeInstaller.Busy)
                {
                    Log.Warn($"service unreachable for {StateUnreachableExitMs} ms; exiting");
                    Native.PostQuitMessage(0);
                }
                return;
            }
            _stateUnreachableSinceTick = 0;
            Apply(state);
        }
        catch (Exception ex)
        {
            Log.Error($"poll failed: {ex.Message}");
        }
    }

    private static void Apply(OverlayState state)
    {
        var previous = _state;
        _state = state;
        // Widgets first: they and the kiosks share the topmost band, and a
        // widget pinned to the panel monitor belongs above it.
        try { ApplyOverlays(previous, state); }
        catch (Exception ex) { Log.Error($"overlay reconcile: {ex.Message}"); }
        try { ApplyPanelKiosk(state); }
        catch (Exception ex) { Log.Error($"panel kiosk reconcile: {ex.Message}"); }
        try { _monitorKiosks?.Reconcile(state.Assignments, _pairedToken); }
        catch (Exception ex) { Log.Error($"monitor-kiosk reconcile: {ex.Message}"); }
        try { _streamHosts?.Reconcile(state.Streams, _pairedToken); }
        catch (Exception ex) { Log.Error($"stream-host reconcile: {ex.Message}"); }
        if (IsIdle()) MaybeArmIdleExitTimer();
        else DisarmIdleExitTimer();
    }

    private static void ApplyPanelKiosk(OverlayState state)
    {
        var kiosk = _panelKiosk is { } k && k.Hwnd != IntPtr.Zero ? k : null;
        if (!state.AutoLaunch)
        {
            if (kiosk is not null) ClosePanelKiosk();
            return;
        }
        if (kiosk is null)
        {
            MaybeShowPanelKiosk();
            return;
        }
        kiosk.SetMonitorGuard(state.ReserveMonitor);
        kiosk.ReassertTaskbar();
        if (kiosk.HasConfirmedContent && !kiosk.Failed) _kioskUnconfirmedRecreates = 0;

        string? reason = null;
        if (kiosk.SeeThrough != IsSeeThrough(state.Y70Backdrop)) reason = "backdrop changed";
        else if (kiosk.CompatibilityRendering != state.Y70CompatibilityRendering) reason = "compatibility rendering changed";
        else if (kiosk.Unhealthy(_kioskUnconfirmedRecreates))
        {
            _kioskUnconfirmedRecreates++;
            reason = $"unhealthy failed={kiosk.Failed} age={kiosk.AgeMs} ms attempt={_kioskUnconfirmedRecreates}";
        }
        else
        {
            var target = PanelDisplay.Find();
            var b = kiosk.MonitorBounds;
            if (target is not null
                && !(target.Bounds.Left == b.Left && target.Bounds.Top == b.Top
                     && target.Bounds.Right == b.Right && target.Bounds.Bottom == b.Bottom))
                reason = $"monitor bounds changed -> {target.Bounds.Width}x{target.Bounds.Height}";
        }
        if (reason is null) return;
        Log.Info($"panel kiosk {reason}; recreating");
        ClosePanelKiosk();
        MaybeShowPanelKiosk();
    }

    private static void ApplyOverlays(OverlayState? previous, OverlayState state)
    {
        var show = ShouldShowOverlays(state);
        if (previous is null || show != ShouldShowOverlays(previous))
        {
            Log.Info($"overlays: show={show} (enabled={state.OverlayEnabled} pinned={state.Pinned})");
            TearDownOverlays();
            if (show) CreateOverlay(state.Monitor, state.AlwaysOnTop);
            return;
        }
        if (!show) return;
        if (state.Monitor != previous.Monitor)
        {
            Log.Info($"overlays: monitor {previous.Monitor} -> {state.Monitor}; respawning");
            TearDownOverlays();
            CreateOverlay(state.Monitor, state.AlwaysOnTop);
            return;
        }
        if (state.AlwaysOnTop == previous.AlwaysOnTop) return;
        foreach (var overlay in Overlays) overlay.SetAlwaysOnTop(state.AlwaysOnTop);
    }
}
