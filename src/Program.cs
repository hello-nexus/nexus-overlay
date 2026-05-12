using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Qos.Overlay.Win32;

namespace Qos.Overlay;

internal static class Program
{
    private const string SingletonMutexName = "Global\\Qos.Overlay.Singleton";
    private const string ServiceOrigin = "http://localhost:9400";
    private const string MarshalerClassName = "Qos.Overlay.Marshaler";
    private const string ShowDashboardMessageName = "Qos.Overlay.ShowDashboard";
    private const string PrefsChangedMessageName = "Qos.Overlay.PrefsChanged";
    private static readonly UIntPtr TIMER_PREFS_POLL = new(1);
    private static readonly UIntPtr TIMER_IDLE_EXIT = new(2);
    // Grace window after going idle (no widgets, dashboard hidden) before
    // the process exits. Gives the user a comfortable margin to flip
    // widgets back on or reopen the dashboard without paying the cold-
    // start cost of relaunching qos-overlay + a fresh WebView2 tree.
    private const uint IdleExitDelayMs = 30_000;

    private static readonly List<OverlayWindow> Overlays = new();
    private static DashboardWindow? _dashboard;
    private static uint _showDashboardMsg;
    private static uint _prefsChangedMsg;
    private static QosApi? _api;
    private static string _pairedToken = "";
    private static IntPtr _marshalerHwnd;
    private static MarshalerOwner? _marshalerOwner;
    private static Win32SynchronizationContext? _syncContext;
    private static bool _lastPolledEnabled;
    private static bool _lastPolledAlwaysOnTop;
    private static int _lastPolledMonitorIndex = -1;

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

        _api = new QosApi(ServiceOrigin);

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
        Log.Info($"prefs enabled={prefs.OverlayWidgetsEnabled} alwaysOnTop={prefs.OverlayWidgetsAlwaysOnTop} monitor={prefs.OverlayWidgetsMonitor}");
        _lastPolledEnabled = prefs.OverlayWidgetsEnabled;
        _lastPolledAlwaysOnTop = prefs.OverlayWidgetsAlwaysOnTop;
        _lastPolledMonitorIndex = prefs.OverlayWidgetsMonitor;

        // Now safe to install: WebView2 callbacks fire on this thread once
        // the message loop is pumping, and the sync context drains via the
        // registered drain message.
        _syncContext = new Win32SynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_syncContext);
        _marshalerOwner = new MarshalerOwner();
        _marshalerHwnd = Win32Window.Create(
            MarshalerClassName,
            "Qos.Overlay.Marshaler",
            0u,
            (uint)Native.WS_EX_TOOLWINDOW,
            0, 0, 0, 0,
            _marshalerOwner);
        _syncContext.Bind(_marshalerHwnd);
        Log.Info($"marshaler hwnd=0x{_marshalerHwnd:X}");

        // Register the cross-process message used by the tray's "Open Qos"
        // path. Both sender (qos-service TrayIcon) and receiver (us) call
        // RegisterWindowMessageW with the same string and get the same ID
        // for the OS session lifetime.
        _showDashboardMsg = Native.RegisterWindowMessageW(ShowDashboardMessageName);
        Log.Info($"registered ShowDashboard msg=0x{_showDashboardMsg:X}");

        // Register the push-notify message that the user-session helper
        // posts from `overlay.prefsChanged`. Receiving it kicks
        // PollPrefsAsync immediately so user-visible toggles feel instant
        // instead of waiting for the next 5 s poll tick.
        _prefsChangedMsg = Native.RegisterWindowMessageW(PrefsChangedMessageName);
        Log.Info($"registered PrefsChanged msg=0x{_prefsChangedMsg:X}");

        // Overlay widgets only spawn when the user has them enabled. The
        // process always stays resident so the tray's "Open Qos" can post
        // ShowDashboard to the marshaler without paying a Chromium cold
        // start, and so the dashboard window can share the WebView2 process
        // tree with any active overlay widgets.
        if (prefs.OverlayWidgetsEnabled)
        {
            CreateOverlay(prefs.OverlayWidgetsMonitor, prefs.OverlayWidgetsAlwaysOnTop);
        }
        else
        {
            Log.Info("desktop widgets disabled; staying resident for on-demand dashboard");
            // Start the idle clock. If the tray is launching us for a
            // dashboard open, the ShowDashboard message arrives within
            // a few hundred ms and disarms it. If we got launched purely
            // by SCM/auto-start with widgets off and nothing follows,
            // we exit after the grace window.
            MaybeArmIdleExitTimer();
        }

        // Pin the overlay's AppID across virtual desktops. Process-level
        // pin, not per-overlay - one call covers every HWND we own. Idempotent
        // (IsAppIdPinned check inside) so repeated overlay restarts don't churn.
        VirtualDesktopPin.TryPinApp();

        // Poll prefs every 5s for changes to the always-on-top toggle.
        Native.SetTimer(_marshalerHwnd, TIMER_PREFS_POLL, 5000, IntPtr.Zero);

        var result = MessageLoop.Run(_syncContext);

        // Cleanup.
        Native.KillTimer(_marshalerHwnd, TIMER_PREFS_POLL);
        foreach (var o in Overlays) o.Dispose();
        Overlays.Clear();
        _dashboard?.Dispose();
        _dashboard = null;
        return result;
    }

    /// <summary>
    /// Create the dashboard on first request; reuse it on subsequent ones.
    /// The dashboard window hides instead of destroying itself on close,
    /// so the second "Open Qos" just needs to show + focus the existing
    /// HWND - no WebView2 cold start.
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
    /// Invoked by DashboardWindow on WM_CLOSE (after the window hides
    /// itself). If no widgets are showing either, arm the idle-exit
    /// timer so the process unloads its WebView2 tree after the grace
    /// window. The dashboard re-show path is fast enough that this
    /// reclaim is invisible to a user who actually comes back.
    /// </summary>
    internal static void OnDashboardHidden()
    {
        MaybeArmIdleExitTimer();
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

            // Enabled toggle: the service no longer kills the overlay
            // process when widgets are disabled (the dashboard window
            // lives here too). We tear down or recreate widget HWNDs
            // in-process. The marshaler and dashboard window are
            // untouched.
            if (latest.OverlayWidgetsEnabled != _lastPolledEnabled)
            {
                Log.Info($"prefs poll: enabled changed {_lastPolledEnabled} -> {latest.OverlayWidgetsEnabled}");
                _lastPolledEnabled = latest.OverlayWidgetsEnabled;
                if (!latest.OverlayWidgetsEnabled)
                {
                    TearDownOverlays();
                    _lastPolledMonitorIndex = latest.OverlayWidgetsMonitor;
                    _lastPolledAlwaysOnTop = latest.OverlayWidgetsAlwaysOnTop;
                    // Going widget-less might leave us fully idle if no
                    // dashboard is visible. Arm the grace timer so the
                    // process reclaims its memory after the cooldown.
                    MaybeArmIdleExitTimer();
                    return;
                }
                // Re-enabled: spawn widget windows again with the
                // freshly-read monitor + always-on-top so the user
                // sees them reappear without waiting for a separate
                // poll cycle.
                DisarmIdleExitTimer();
                CreateOverlay(latest.OverlayWidgetsMonitor, latest.OverlayWidgetsAlwaysOnTop);
                _lastPolledMonitorIndex = latest.OverlayWidgetsMonitor;
                _lastPolledAlwaysOnTop = latest.OverlayWidgetsAlwaysOnTop;
                return;
            }

            // While widgets are disabled, the remaining poll branches
            // would try to mutate non-existent overlays. Skip them.
            if (!_lastPolledEnabled) return;

            // Monitor index change: tear down the existing overlay (and its
            // WebView2 process tree) and respawn on the new monitor. Pref
            // poll fires on the message-loop thread, so the dispose +
            // recreate is single-threaded with WM_DESTROY handlers - no race.
            if (latest.OverlayWidgetsMonitor != _lastPolledMonitorIndex)
            {
                Log.Info($"prefs poll: monitor index changed {_lastPolledMonitorIndex} -> {latest.OverlayWidgetsMonitor}, respawning");
                _lastPolledMonitorIndex = latest.OverlayWidgetsMonitor;
                TearDownOverlays();
                CreateOverlay(latest.OverlayWidgetsMonitor, latest.OverlayWidgetsAlwaysOnTop);
                _lastPolledAlwaysOnTop = latest.OverlayWidgetsAlwaysOnTop;
                return;
            }

            // Always-on-top change: just re-apply on the existing overlay.
            // The guard prevents clobbering any in-flight SPA-pushed override.
            if (latest.OverlayWidgetsAlwaysOnTop == _lastPolledAlwaysOnTop) return;
            _lastPolledAlwaysOnTop = latest.OverlayWidgetsAlwaysOnTop;
            foreach (var overlay in Overlays)
            {
                overlay.SetAlwaysOnTop(latest.OverlayWidgetsAlwaysOnTop);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"prefs poll failed: {ex.Message}");
        }
    }
}
