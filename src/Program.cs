using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Qos.Overlay.Win32;

namespace Qos.Overlay;

internal static class Program
{
    private const string SingletonMutexName = "Global\\Qos.Overlay.Singleton";
    private const string ServiceOrigin = "http://localhost:9400";

    private static readonly List<OverlayWindow> Overlays = new();
    private static qOSApi? _api;
    private static System.Windows.Forms.Timer? _prefsTimer;

    /// <summary>
    /// Apply Z-order toggle to every overlay window. Called by the SPA bridge
    /// when the user clicks the desktop context menu's "Always on top" so the
    /// toggle takes effect instantly instead of waiting for the prefs poll.
    /// </summary>
    public static void SetAllAlwaysOnTop(bool value)
    {
        foreach (var overlay in Overlays)
        {
            try { overlay.SetAlwaysOnTop(value); } catch { /* best-effort */ }
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Reset();
        Log.Info($"main start args=[{string.Join(' ', args)}]");

        // Single-instance guard: the service spawns idempotently on every
        // start, so a second launch must exit cleanly without disturbing
        // the running host.
        using var mutex = new Mutex(initiallyOwned: true, SingletonMutexName, out var firstInstance);
        if (!firstInstance)
        {
            Log.Info("singleton: another instance owns the mutex; exiting");
            return 0;
        }

        // Per-monitor V2 DPI awareness so each overlay scales to its
        // monitor without blurring across mixed-DPI layouts. Falls back
        // gracefully on Win10 < 1703.
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _api = new qOSApi(ServiceOrigin);

        // Pair + initial prefs synchronously before the message loop starts.
        // If the service isn't up, exit and let the launcher retry us.
        var token = _api.PairAsync().GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(token))
        {
            Log.Error("pair returned empty token; service unreachable?");
            return 2;
        }
        Log.Info($"paired ok token len={token.Length}");

        var prefs = _api.GetPreferencesAsync().GetAwaiter().GetResult();
        Log.Info($"prefs enabled={prefs.OverlayWidgetsEnabled} alwaysOnTop={prefs.OverlayWidgetsAlwaysOnTop}");
        if (!prefs.OverlayWidgetsEnabled)
        {
            Log.Info("desktop widgets disabled; exiting");
            return 0;
        }

        CreateOverlays(token, prefs.OverlayWidgetsAlwaysOnTop);

        // Poll prefs every 5s for changes to the always-on-top toggle. Only
        // re-apply when the persisted value has actually changed since our
        // last poll - otherwise we would clobber any in-flight override the
        // SPA pushed via setAlwaysOnTop (e.g. while an edit sheet is open),
        // which the previous unconditional re-assertion did within ~5s of
        // any edit menu opening.
        var lastPolledAlwaysOnTop = prefs.OverlayWidgetsAlwaysOnTop;
        _prefsTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _prefsTimer.Tick += async (_, _) =>
        {
            if (_api is null) return;
            var latest = await _api.GetPreferencesAsync();
            if (latest.OverlayWidgetsAlwaysOnTop == lastPolledAlwaysOnTop) return;
            lastPolledAlwaysOnTop = latest.OverlayWidgetsAlwaysOnTop;
            foreach (var overlay in Overlays)
            {
                overlay.SetAlwaysOnTop(latest.OverlayWidgetsAlwaysOnTop);
            }
        };
        _prefsTimer.Start();

        Application.Run();
        return 0;
    }

    private static void CreateOverlays(string token, bool alwaysOnTop)
    {
        var monitors = Monitors.Enumerate();
        Log.Info($"enumerated monitors count={monitors.Count}");
        foreach (var monitor in monitors)
        {
            var url = $"{ServiceOrigin}/overlay?monitor={monitor.Index}&token={Uri.EscapeDataString(token)}";
            var overlay = new OverlayWindow(monitor, url, alwaysOnTop);
            overlay.FormClosed += (_, _) =>
            {
                Overlays.Remove(overlay);
                if (Overlays.Count == 0) Application.ExitThread();
            };
            Overlays.Add(overlay);
            overlay.Show();
        }
    }
}
