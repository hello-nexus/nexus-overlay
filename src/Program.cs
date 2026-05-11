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
    private static readonly UIntPtr TIMER_PREFS_POLL = new(1);

    private static readonly List<OverlayWindow> Overlays = new();
    private static QosApi? _api;
    private static string _pairedToken = "";
    private static IntPtr _marshalerHwnd;
    private static MarshalerOwner? _marshalerOwner;
    private static Win32SynchronizationContext? _syncContext;
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
        if (!prefs.OverlayWidgetsEnabled)
        {
            Log.Info("desktop widgets disabled; exiting");
            return 0;
        }
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

        CreateOverlay(prefs.OverlayWidgetsMonitor, prefs.OverlayWidgetsAlwaysOnTop);

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
        return result;
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
    private sealed class MarshalerOwner : IWin32WindowOwner
    {
        public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == Native.WM_TIMER && wParam == (IntPtr)1)
            {
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
