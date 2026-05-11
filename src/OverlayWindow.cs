using System;
using System.Drawing;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Qos.Overlay.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Qos.Overlay;

/// <summary>
/// One overlay per HMONITOR. WebView2 fills the form, loads the
/// qos-service /overlay SPA, and posts widget rects back via
/// webMessageReceived. The host carves SetWindowRgn so areas outside
/// widgets are not part of the window at all (input falls through, no
/// pixels painted).
///
/// Rendering is always per-pixel alpha: WebView2's
/// DefaultBackgroundColor is Color.Transparent so the swapchain exposes
/// alpha pixels and the widget cards composite over the desktop
/// wallpaper. Widget tiles are responsible for their own card surface
/// (the SCSS panel-card token drives the visible fill / border / shadow).
/// SetWindowRgn carve-out still applies so clicks outside widget rects
/// fall through to the desktop.
/// </summary>
internal sealed class OverlayWindow : Form
{
    // WinForms top-level windows cannot have a truly transparent BackColor
    // (the setter throws on Color.Transparent). Keep this opaque dark stub
    // so the Form initializes cleanly; it is never visible at runtime
    // because (a) SetWindowRgn carves the form down to the widget rects
    // and (b) WebView2 paints transparent pixels over what would otherwise
    // be the form background.
    private static readonly Color FormStubBackColor = Color.FromArgb(20, 20, 20);

    private readonly MonitorInfo _monitor;
    private readonly string _navigationUrl;
    private readonly WebView2 _webView;
    private bool _alwaysOnTop;
    private bool _zOrderApplied;

    public OverlayWindow(MonitorInfo monitor, string navigationUrl, bool alwaysOnTop)
    {
        _monitor = monitor;
        _navigationUrl = navigationUrl;
        _alwaysOnTop = alwaysOnTop;

        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = FormStubBackColor;
        TopMost = false;
        Text = $"Qos Overlay ({monitor.Index})";

        var bounds = monitor.Bounds;
        Location = new Point(bounds.Left, bounds.Top);
        Size = new Size(bounds.Width, bounds.Height);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            // Per-pixel alpha: transparent default bg lets the WebView2
            // swapchain expose alpha to the desktop behind. Widget cards
            // paint their own opaque surface via CSS so the wallpaper
            // shows only where the SPA leaves alpha-0 pixels.
            DefaultBackgroundColor = Color.Transparent,
        };
        _webView.CoreWebView2InitializationCompleted += OnCoreInitialized;
        _webView.WebMessageReceived += OnWebMessage;
        _webView.NavigationCompleted += OnNavigationCompleted;

        Controls.Add(_webView);
        ResumeLayout(performLayout: false);

        Log.Info($"overlay ctor monitor={monitor.Index} bounds={bounds.Left},{bounds.Top},{bounds.Width}x{bounds.Height} url={navigationUrl}");
    }

    public void SetAlwaysOnTop(bool value)
    {
        if (_alwaysOnTop == value && _zOrderApplied) return;
        _alwaysOnTop = value;
        ApplyZOrder();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Hide everything until the SPA reports widget rects. 1x1 region in
        // the corner is invisible enough; switching to a real region
        // happens on the first reportLayout webMessage.
        var emptyRgn = Native.CreateRectRgn(0, 0, 1, 1);
        Native.SetWindowRgn(Handle, emptyRgn, false);
        Log.Info($"overlay handle created hwnd={Handle}");
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            await InitializeWebViewAsync();
            Log.Info("overlay webview init done");
        }
        catch (Exception ex)
        {
            Log.Error($"overlay webview init failed: {ex}");
        }
        ApplyZOrder();
    }

    private async Task InitializeWebViewAsync()
    {
        // CommonApplicationData (%ProgramData%) instead of LocalApplicationData
        // because qos-overlay is spawned by the SYSTEM service. For SYSTEM,
        // LocalApplicationData resolves to C:\Windows\System32\config\
        // systemprofile\AppData\Local\, which WebView2 refuses to use - it
        // surfaces a "Microsoft Edge can't read and write to its data
        // directory" dialog and aborts init. ProgramData is writable by both
        // SYSTEM and the user, so the choice is forward-compatible if the
        // overlay ever runs in user-mode (e.g., a future broker split).
        var userDataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Qos", "DesktopWebView2");
        var options = new CoreWebView2EnvironmentOptions
        {
            // Force every WebView2 in this process into one renderer process.
            // Together with site-isolation off, every monitor's overlay shares
            // the same Chromium renderer so RAM stays flat as monitors scale.
            AdditionalBrowserArguments =
                "--renderer-process-limit=1 " +
                "--disable-site-isolation-trials " +
                "--disable-features=IsolateOrigins,site-per-process",
        };
        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
        await _webView.EnsureCoreWebView2Async(env);
    }

    private void OnCoreInitialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            Log.Error($"overlay core init failed: {e.InitializationException}");
            return;
        }
        var settings = _webView.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        Log.Info($"overlay core init success; navigating {_navigationUrl}");
        _webView.CoreWebView2.Navigate(_navigationUrl);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Log.Info($"overlay navigation completed isSuccess={e.IsSuccess} status={e.HttpStatusCode} webErr={e.WebErrorStatus}");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            if (type == "reportLayout")
            {
                ApplyHitRegion(root);
            }
            else if (type == "setAlwaysOnTop"
                && root.TryGetProperty("value", out var valueEl)
                && (valueEl.ValueKind == JsonValueKind.True || valueEl.ValueKind == JsonValueKind.False))
            {
                // Apply to every monitor's overlay so a toggle in one
                // window updates them all simultaneously.
                Program.SetAllAlwaysOnTop(valueEl.GetBoolean());
            }
        }
        catch (Exception ex)
        {
            Log.Error($"webMessage parse error: {ex.Message}");
        }
    }

    private void ApplyHitRegion(JsonElement root)
    {
        var combined = Native.CreateRectRgn(0, 0, 0, 0);
        var added = 0;

        if (root.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
        {
            foreach (var widget in widgets.EnumerateArray())
            {
                if (TryReadRect(widget, out var rect))
                {
                    AddRect(combined, rect);
                    added++;
                }
            }
        }

        if (root.TryGetProperty("popover", out var popover) && popover.ValueKind == JsonValueKind.Object)
        {
            if (TryReadRect(popover, out var rect))
            {
                AddRect(combined, rect);
                added++;
            }
        }

        IntPtr region;
        if (added > 0)
        {
            region = combined;
        }
        else
        {
            Native.DeleteObject(combined);
            region = Native.CreateRectRgn(0, 0, 1, 1);
        }

        Native.SetWindowRgn(Handle, region, true);
        Log.Info($"region rebuilt rects={added}");
    }

    // Corner radius to match the panel-card --radius token (10 px). The
    // ellipse axes passed to CreateRoundRectRgn are 2*radius, not the
    // radius itself.
    private const int CornerEllipsePx = 20;

    private static void AddRect(IntPtr destination, (int x, int y, int w, int h) r)
    {
        var rect = Native.CreateRoundRectRgn(
            r.x, r.y, r.x + r.w, r.y + r.h,
            CornerEllipsePx, CornerEllipsePx);
        Native.CombineRgn(destination, destination, rect, Native.RGN_OR);
        Native.DeleteObject(rect);
    }

    private static bool TryReadRect(JsonElement el, out (int x, int y, int w, int h) rect)
    {
        rect = (0, 0, 0, 0);
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("x", out var xEl) || !el.TryGetProperty("y", out var yEl)) return false;
        if (!el.TryGetProperty("w", out var wEl) || !el.TryGetProperty("h", out var hEl)) return false;
        rect = (
            (int)Math.Round(xEl.GetDouble()),
            (int)Math.Round(yEl.GetDouble()),
            (int)Math.Round(wEl.GetDouble()),
            (int)Math.Round(hEl.GetDouble()));
        return rect.w > 0 && rect.h > 0;
    }

    private void ApplyZOrder()
    {
        if (!IsHandleCreated) return;
        // Detach from any prior parent (e.g. WorkerW from a previous mode)
        // before reapplying Z-order. Both branches need the form back as a
        // top-level window first.
        Native.SetParent(Handle, IntPtr.Zero);

        if (_alwaysOnTop)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Log.Info("zorder=topmost");
        }
        else
        {
            // "Stay below" — sit above wallpaper/icons, below every normal
            // window. Predictable across Win10/11 builds; doesn't depend on
            // the WorkerW parenting trick which behaves inconsistently.
            // Trade-off: Win+D minimizes the overlay (it's a top-level
            // window). We accept that for v1.
            Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Log.Info("zorder=bottom");
        }
        _zOrderApplied = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _webView.Dispose();
        }
        base.Dispose(disposing);
    }
}
