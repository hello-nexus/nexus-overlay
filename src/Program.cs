using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
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
    private const string ShowPanelKioskMessageName = "Nexus.Overlay.ShowPanelKiosk";
    private const string HidePanelKioskMessageName = "Nexus.Overlay.HidePanelKiosk";
    private const string PrefsChangedMessageName = "Nexus.Overlay.PrefsChanged";
    private static readonly UIntPtr TIMER_PREFS_POLL = new(1);
    private static readonly UIntPtr TIMER_IDLE_EXIT = new(2);
    // Grace window after going idle (no widgets, dashboard hidden) before
    // the process exits. Just long enough to absorb the tray's
    // launch -> ShowDashboard message race at startup; not meant as a
    // user-facing "keep around in case they come back" window.
    private const uint IdleExitDelayMs = 3_000;

    private static readonly List<OverlayWindow> Overlays = new();
    private static DashboardWindow? _dashboard;
    private static PanelKioskWindow? _panelKiosk;
    private static uint _showDashboardMsg;
    private static uint _showPanelKioskMsg;
    private static uint _hidePanelKioskMsg;
    private static uint _prefsChangedMsg;
    private static NexusApi? _api;
    private static string _pairedToken = "";
    private static IntPtr _marshalerHwnd;
    private static MarshalerOwner? _marshalerOwner;
    private static Win32SynchronizationContext? _syncContext;
    // "Should we be showing overlay widgets right now?" — enabled toggle
    // AND at least one widget pinned. Either condition flipping false
    // is treated identically: tear down + idle.
    private static bool _lastPolledShouldShow;
    private static bool _lastPolledAlwaysOnTop;
    private static int _lastPolledMonitorIndex = -1;
    // Whether the panel-monitor guard should run. Mirrored from the
    // panel.reserveMonitor pref; a prefs change flips the guard on the live
    // kiosk without recreating it.
    private static bool _lastPolledReserveMonitor = true;

    private static bool ShouldShowOverlays(UiPrefs p)
        => p.Overlay.Enabled && p.Overlay.Layout.Count > 0;

    public static void SetAllAlwaysOnTop(bool value)
    {
        foreach (var overlay in Overlays)
        {
            try { overlay.SetAlwaysOnTop(value); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Move the overlay (we only have one in single-monitor mode, but
    /// loop for symmetry) to a new monitor in-place. Triggered by the
    /// SPA's <c>setMonitor</c> webMessage so the user sees the move
    /// land instantly instead of waiting for the 5 s prefs poll.
    /// Also updates the poll's last-seen index so the subsequent
    /// poll doesn't fire a redundant teardown+respawn.
    /// </summary>
    public static void MoveAllToMonitor(int value)
    {
        _lastPolledMonitorIndex = value;
        foreach (var overlay in Overlays)
        {
            try { overlay.MoveToMonitor(value); } catch (Exception ex) { Log.Error($"MoveToMonitor: {ex.Message}"); }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Reset();
        Log.Info($"main start args=[{string.Join(' ', args)}]");

        using var mutex = new Mutex(initiallyOwned: true, SingletonMutexName, out var firstInstance);
        if (!firstInstance)
        {
            Log.Info("singleton: another instance owns the mutex; exiting");
            return 0;
        }

        // Set the AppUserModelID before any window is created so the
        // shell associates every overlay HWND with this AUMID. Required
        // for the cross-desktop pin via PinAppID later.
        VirtualDesktopPin.SetProcessAppId();

        // Per-monitor V2 DPI awareness. Win10 < 1703 will fail this; we
        // accept the older behavior on those (best-effort).
        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        // WebView2 requires STA. CoInitializeEx is benign if STAThread
        // already initialized; we just ensure the apartment is what we expect.
        Native.CoInitializeEx(IntPtr.Zero, Native.COINIT_APARTMENTTHREADED);

        _api = new NexusApi(ServiceOrigin);

        // Pair + initial prefs synchronously before the message loop or
        // sync context exist. The HttpClient await chain must NOT capture
        // our Win32SynchronizationContext here - if it did, the continuation
        // would post back to the not-yet-running message loop and deadlock.
        _pairedToken = _api.PairAsync().GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(_pairedToken))
        {
            Log.Error("pair returned empty token; service unreachable?");
            return 2;
        }
        Log.Info($"paired ok token len={_pairedToken.Length}");

        var prefs = _api.GetPreferencesAsync().GetAwaiter().GetResult();
        Log.Info($"prefs enabled={prefs.Overlay.Enabled} pinned={prefs.Overlay.Layout.Count} alwaysOnTop={prefs.Overlay.AlwaysOnTop} monitor={prefs.Overlay.Monitor}");
        _lastPolledShouldShow = ShouldShowOverlays(prefs);
        _lastPolledAlwaysOnTop = prefs.Overlay.AlwaysOnTop;
        _lastPolledMonitorIndex = prefs.Overlay.Monitor;
        _lastPolledReserveMonitor = prefs.Panel.ReserveMonitor;

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

        // Register the cross-process message used by the tray's "Open Nexus"
        // path. Both sender (nexus-service TrayIcon) and receiver (us) call
        // RegisterWindowMessageW with the same string and get the same ID
        // for the OS session lifetime.
        _showDashboardMsg = Native.RegisterWindowMessageW(ShowDashboardMessageName);
        Log.Info($"registered ShowDashboard msg=0x{_showDashboardMsg:X}");

        _showPanelKioskMsg = Native.RegisterWindowMessageW(ShowPanelKioskMessageName);
        Log.Info($"registered ShowPanelKiosk msg=0x{_showPanelKioskMsg:X}");

        _hidePanelKioskMsg = Native.RegisterWindowMessageW(HidePanelKioskMessageName);
        Log.Info($"registered HidePanelKiosk msg=0x{_hidePanelKioskMsg:X}");

        // Register the push-notify message that the user-session helper
        // posts from `overlay.prefsChanged`. Receiving it kicks
        // PollPrefsAsync immediately so user-visible toggles feel instant
        // instead of waiting for the next 5 s poll tick.
        _prefsChangedMsg = Native.RegisterWindowMessageW(PrefsChangedMessageName);
        Log.Info($"registered PrefsChanged msg=0x{_prefsChangedMsg:X}");

        // Overlay widgets only spawn when the toggle is on AND at least
        // one widget is pinned. With either condition false we stay
        // resident only long enough for the tray's "Open Nexus" to post
        // ShowDashboard; otherwise we idle out after the grace window.
        if (_lastPolledShouldShow)
        {
            CreateOverlay(prefs.Overlay.Monitor, prefs.Overlay.AlwaysOnTop);
        }
        else
        {
            Log.Info($"no overlay widgets to show (enabled={prefs.Overlay.Enabled} pinned={prefs.Overlay.Layout.Count}); staying resident for on-demand dashboard");
        }

        // Panel kiosk auto-launch: when panel.autoLaunch is on AND a
        // recognized HYTE touch panel is connected, open the fullscreen
        // kiosk window on it. Swallow exceptions so a kiosk-init failure
        // doesn't take down the whole overlay process before the message
        // loop is even up.
        if (prefs.Panel.AutoLaunch)
        {
            try { MaybeShowPanelKiosk(); }
            catch (Exception ex) { Log.Error($"startup MaybeShowPanelKiosk: {ex.Message}"); }
        }

        // Arm the idle-exit timer only if nothing landed on screen. With any
        // of overlays/kiosk/dashboard up, IsIdle returns false and the call
        // no-ops.
        MaybeArmIdleExitTimer();

        // Pin the overlay's AppID across virtual desktops. Process-level
        // pin, not per-overlay - one call covers every HWND we own. Idempotent
        // (IsAppIdPinned check inside) so repeated overlay restarts don't churn.
        VirtualDesktopPin.TryPinApp();

        // Poll prefs every 5s for changes to the always-on-top toggle.
        Native.SetTimer(_marshalerHwnd, TIMER_PREFS_POLL, 5000, IntPtr.Zero);

        var result = MessageLoop.Run(_syncContext);

        // Cleanup. Dispose the kiosk first, then dashboard, then per-monitor
        // overlays — mirrors the on-screen reverse z-order so the dispose
        // chain progresses through windows by visual prominence.
        Native.KillTimer(_marshalerHwnd, TIMER_PREFS_POLL);
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
    /// fresh WebView2 init + navigation — important after a wwwroot
    /// redeploy. The cold start is ~1-2s.
    /// </summary>
    private static void ShowOrCreateDashboard()
    {
        // Dashboard is appearing - cancel any pending idle exit.
        DisarmIdleExitTimer();
        if (_dashboard is null)
        {
            var url = $"{ServiceOrigin}/?token={Uri.EscapeDataString(_pairedToken)}";
            _dashboard = new DashboardWindow(url);
            Log.Info("dashboard created");
            return;
        }
        _dashboard.ShowAndFocus();
        Log.Info("dashboard focused");
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
        if (target is null)
        {
            Log.Info("panel kiosk skipped: no recognized HYTE panel display connected");
            return;
        }
        DisarmIdleExitTimer();
        var url = $"{ServiceOrigin}/panel?token={Uri.EscapeDataString(_pairedToken)}";
        _panelKiosk = new PanelKioskWindow(target, url, _lastPolledReserveMonitor);
        Log.Info($"panel kiosk opened on monitor={target.Index} guard={_lastPolledReserveMonitor}");
    }

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
        return true;
    }

    private static void MaybeArmIdleExitTimer()
    {
        if (_marshalerHwnd == IntPtr.Zero) return;
        if (!IsIdle()) return;
        Native.SetTimer(_marshalerHwnd, TIMER_IDLE_EXIT, IdleExitDelayMs, IntPtr.Zero);
        Log.Info($"idle: arming exit timer for {IdleExitDelayMs} ms");
    }

    private static void DisarmIdleExitTimer()
    {
        if (_marshalerHwnd == IntPtr.Zero) return;
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
        foreach (var o in Overlays) o.Dispose();
        Overlays.Clear();
        Log.Info("overlays torn down");
    }

    // Marshaler-window owner: handles WM_TIMER for the prefs poll and
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
                _ = PollPrefsAsync();
                return IntPtr.Zero;
            }
            if (msg == Native.WM_TIMER && wParam == (IntPtr)(long)TIMER_IDLE_EXIT.ToUInt64())
            {
                // Idle grace window elapsed. Confirm we're still idle
                // (a widget toggle or dashboard reopen between arm and
                // fire would have killed the timer, but a defensive
                // re-check guards against any in-flight race).
                Native.KillTimer(_marshalerHwnd, TIMER_IDLE_EXIT);
                if (IsIdle())
                {
                    Log.Info("idle exit: no widgets, dashboard hidden; quitting");
                    Native.PostQuitMessage(0);
                }
                return IntPtr.Zero;
            }
            if (_showDashboardMsg != 0 && msg == _showDashboardMsg)
            {
                try { ShowOrCreateDashboard(); }
                catch (Exception ex) { Log.Error($"ShowOrCreateDashboard: {ex.Message}"); }
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
                _ = PollPrefsAsync();
                return IntPtr.Zero;
            }
            return null;
        }
    }

    private static async System.Threading.Tasks.Task PollPrefsAsync()
    {
        if (_api is null) return;
        try
        {
            var latest = await _api.GetPreferencesAsync();

            // Reconcile kiosk state against the toggle. Using actual window
            // presence (not a cached pref value) means a Y70 hot-plug AFTER
            // panel.autoLaunch was already on gets picked up on the next
            // poll: previous poll's Find() returned null, this poll finds it.
            var kioskUp = _panelKiosk is not null;
            if (latest.Panel.AutoLaunch && !kioskUp) MaybeShowPanelKiosk();
            else if (!latest.Panel.AutoLaunch && kioskUp) ClosePanelKiosk();

            // Toggle the monitor guard on the live kiosk when reserveMonitor
            // flips. The service pushes PrefsChanged on any settings write, so
            // this runs promptly off that signal — no extra poll. Track the
            // value even when no kiosk is up so the next launch picks it up.
            if (latest.Panel.ReserveMonitor != _lastPolledReserveMonitor)
            {
                _lastPolledReserveMonitor = latest.Panel.ReserveMonitor;
                _panelKiosk?.SetMonitorGuard(_lastPolledReserveMonitor);
                Log.Info($"prefs poll: reserveMonitor -> {_lastPolledReserveMonitor}");
            }

            // "Should overlays be visible?" = toggle on AND at least one
            // pinned widget. The service no longer kills this process
            // when overlays go away (the dashboard window lives here
            // too); we tear down widget HWNDs in-process and idle out.
            var nowShouldShow = ShouldShowOverlays(latest);
            if (nowShouldShow != _lastPolledShouldShow)
            {
                Log.Info($"prefs poll: shouldShow changed {_lastPolledShouldShow} -> {nowShouldShow} (enabled={latest.Overlay.Enabled} pinned={latest.Overlay.Layout.Count})");
                _lastPolledShouldShow = nowShouldShow;
                if (!nowShouldShow)
                {
                    TearDownOverlays();
                    _lastPolledMonitorIndex = latest.Overlay.Monitor;
                    _lastPolledAlwaysOnTop = latest.Overlay.AlwaysOnTop;
                    MaybeArmIdleExitTimer();
                    return;
                }
                DisarmIdleExitTimer();
                CreateOverlay(latest.Overlay.Monitor, latest.Overlay.AlwaysOnTop);
                _lastPolledMonitorIndex = latest.Overlay.Monitor;
                _lastPolledAlwaysOnTop = latest.Overlay.AlwaysOnTop;
                return;
            }

            // Nothing pinned / toggle off: skip downstream branches that
            // would mutate non-existent overlay HWNDs.
            if (!_lastPolledShouldShow) return;

            // Monitor index change: tear down the existing overlay (and its
            // WebView2 process tree) and respawn on the new monitor. Pref
            // poll fires on the message-loop thread, so the dispose +
            // recreate is single-threaded with WM_DESTROY handlers - no race.
            if (latest.Overlay.Monitor != _lastPolledMonitorIndex)
            {
                Log.Info($"prefs poll: monitor index changed {_lastPolledMonitorIndex} -> {latest.Overlay.Monitor}, respawning");
                _lastPolledMonitorIndex = latest.Overlay.Monitor;
                TearDownOverlays();
                CreateOverlay(latest.Overlay.Monitor, latest.Overlay.AlwaysOnTop);
                _lastPolledAlwaysOnTop = latest.Overlay.AlwaysOnTop;
                return;
            }

            // Always-on-top change: just re-apply on the existing overlay.
            // The guard prevents clobbering any in-flight SPA-pushed override.
            if (latest.Overlay.AlwaysOnTop == _lastPolledAlwaysOnTop) return;
            _lastPolledAlwaysOnTop = latest.Overlay.AlwaysOnTop;
            foreach (var overlay in Overlays)
            {
                overlay.SetAlwaysOnTop(latest.Overlay.AlwaysOnTop);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"prefs poll failed: {ex.Message}");
        }
    }
}
