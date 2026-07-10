using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Top-level WebView2 window hosting the main Nexus dashboard URL. Lives in
/// the same process as the floating overlay widgets so its renderer shares
/// the browser / GPU / network / utility process tree already running for
/// them - the memory win over msedge --app. Singleton per process (the
/// overlay process is itself singleton via the mutex in Program.cs).
/// </summary>
internal sealed unsafe class DashboardWindow : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.Dashboard";
    // Empty caption so the system never has a string to draw if it ever
    // shows the non-client area (e.g. Alt+Space system menu). The custom
    // title bar (handled below in WM_NCCALCSIZE / WM_NCHITTEST) extends the
    // client area to the top of the window so there is no visible caption.
    private const string WindowTitle = "";
    private const int DefaultClientWidth = 1280;
    private const int DefaultClientHeight = 800;
    // Match nexus-web's .layout CSS min-width so the OS refuses to drag the
    // window narrower than the React layout's minimum (avoids a horizontal
    // scrollbar). Keep in lockstep with App.module.scss .layout min-width.
    private const int MinClientWidth = 1000;
    private const int MinClientHeight = 640;
    // Custom title bar logical height (96 DPI). Matches the system caption
    // height and the height of our React-side custom caption buttons +
    // drag strip.
    private const int CustomTitleBarHeightLogical = 32;
    // Resize-grab non-client strip width per edge, logical px at 96 DPI. The
    // default WS_THICKFRAME area is 8px (4 SM_CXFRAME + 4 SM_CXPADDEDBORDER);
    // the WebView2 child covers the client rect, leaving only this edge strip
    // to grab. 12 matches Edge / Settings / Microsoft Store custom frames.
    private const int ResizeGrabLogical = 12;
    private const uint WM_INIT_CONTROLLER = Native.WM_USER + 2;
    private const int PermissionStateDeny = 2;
    // Fully-transparent COREWEBVIEW2_COLOR for the WebView2 controller's
    // default backdrop so the DWM Mica system backdrop shows through wherever
    // the page paints transparent. The nexus-web page renders a transparent
    // backdrop in the Windows shell, so this lets Mica be the window
    // background. Note the struct's byte order is {A,R,G,B} - the LOW byte of
    // this uint is alpha, not the high one - and put_DefaultBackgroundColor
    // rejects any alpha besides fully opaque / fully transparent.
    private const uint DefaultBgTransparent = 0x00000000u;

    private static readonly ConcurrentDictionary<int, DashboardWindow> _instances = new();
    private static int _nextInstanceId;
    private readonly int _instanceId;

    public IntPtr Hwnd { get; private set; }

    // Mutable so a deep-link arriving mid-init (Navigate before CoreWebView2 is
    // ready) redirects the pending InitController navigation instead of no-oping.
    private string _navigationUrl;
    private IntPtr _env;
    private IntPtr _envCreatedHandler;
    private IntPtr _ctrlCreatedHandler;
    private IntPtr _navStartingHandler;
    private IntPtr _newWindowHandler;
    private IntPtr _permissionHandler;
    private IntPtr _webMessageHandler;
    private IntPtr _controller;
    private IntPtr _controller2;
    private IntPtr _coreWebView2;
    private long _navStartingToken;
    private long _newWindowToken;
    private long _permissionToken;
    private long _webMessageToken;
    private bool _disposed;
    private bool _saveOnClose;
    // Last WM_SIZE kind, for skipping the per-tick repeats during an
    // interactive resize (see WM_SIZE handler).
    private int _lastSizeKind = Native.SIZE_RESTORED;
    // Resolved in-app (dashboard) theme, pushed by the page via the
    // "nexus:theme-dark" / "nexus:theme-light" web message. Drives the
    // immersive/Mica theme so it follows the app theme, not the OS theme.
    private bool _appDark;

    public DashboardWindow(string navigationUrl)
    {
        _instanceId = Interlocked.Increment(ref _nextInstanceId);
        _instances[_instanceId] = this;
        _navigationUrl = navigationUrl;

        var (x, y, w, h) = ResolveInitialBounds();

        // Load the Nexus app icon from the installed .ico for the taskbar /
        // Alt+Tab. No visible title bar, so the system can't infer one; load
        // explicitly.
        var hIcon = TryLoadAppIcon();

        Hwnd = Win32Window.Create(
            WindowClassName,
            WindowTitle,
            // WS_SYSMENU is deliberately absent: with the sheet-of-glass
            // extended frame, DWM paints the system caption buttons through
            // the transparent WebView2, doubling the web-drawn min / max /
            // close (DWMWA_CAPTION_COLOR none suppresses the caption bar but
            // not the buttons). Without WS_SYSMENU there are no system
            // buttons to paint, while WS_MINIMIZEBOX / WS_MAXIMIZEBOX keep
            // Aero Snap, double-click-maximize and Win+Arrow semantics, and
            // the web buttons drive the window over IPC (HandleWindowAction).
            (Native.WS_OVERLAPPEDWINDOW & ~Native.WS_SYSMENU)
                | Native.WS_CLIPCHILDREN | Native.WS_CLIPSIBLINGS,
            0u,
            x, y, w, h,
            this,
            IntPtr.Zero, // no class background brush: an opaque client brush
                         // would paint over the Mica system backdrop
            hIcon);

        // Also attach the icon to the window via WM_SETICON. This guarantees
        // the taskbar / Alt+Tab use it even when the OS happens to prefer
        // the window's per-instance icon over the class icon.
        if (hIcon != IntPtr.Zero)
        {
            Native.SendMessageW(Hwnd, Native.WM_SETICON, (IntPtr)Native.ICON_SMALL, hIcon);
            Native.SendMessageW(Hwnd, Native.WM_SETICON, (IntPtr)Native.ICON_BIG, hIcon);
        }

        // Apply the immersive theme to the non-client (caption + frame) area.
        // Seed from the OS theme as a sane default; the page overrides it via
        // the "nexus:theme-dark" / "nexus:theme-light" web message so the
        // immersive/Mica theme follows the in-app theme, not the OS theme.
        _appDark = IsSystemDarkMode();
        ApplyImmersiveTheme(Hwnd, _appDark);

        // Sheet-of-glass frame + Windows 11 Mica system backdrop behind the
        // (transparent) WebView2. See WM_NCCALCSIZE / WM_NCHITTEST below for
        // the matching client expansion + drag region logic.
        ApplyGlassFrame();

        // Force a WM_NCCALCSIZE pass with the new frame settings before
        // the window is first shown. Without this the system may cache
        // the standard-frame measurements from the initial CreateWindow
        // pass and paint a thin title-bar artefact during the first
        // ShowWindow call.
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER
            | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);

        Log.Info($"dashboard ctor bounds={x},{y},{w}x{h} hwnd=0x{Hwnd:X} url={navigationUrl}");
        StartWebView2Init();
    }

    private static void ApplyCustomFrameMargins(IntPtr hwnd)
    {
        // Sheet-of-glass margins: the whole window surface is extended frame.
        // The previous thin top-sliver margin made DWM permanently drop the
        // Mica backdrop on the maximize transition (composing black behind
        // the transparent WebView2 until the window was recreated) - the same
        // root cause as electron#41824, whose fix is this margin shape. Thin
        // margins go black on maximize even when applied once and never
        // re-issued - the shape itself is load-bearing, do not shrink it.
        // The web draws its own min / max / close; the window style omits
        // WS_SYSMENU so DWM has no system caption buttons to paint into the
        // full-window frame (they would show through the transparent
        // WebView2 as a doubled set).
        var margins = new Native.MARGINS
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1,
        };
        var hr = Native.DwmExtendFrameIntoClientArea(hwnd, in margins);
        if (hr != 0)
        {
            Log.Error($"dashboard DwmExtendFrameIntoClientArea hr=0x{hr:X8}");
        }
    }

    private static int ResizeBorderThickness(IntPtr hwnd)
    {
        // Physical pixels for the resize affordance on the side / bottom
        // edges. Scale ResizeGrabLogical to the window's DPI so the grab
        // strip stays the same physical thickness on hi-DPI monitors as
        // it does at 100%.
        var dpi = Native.GetDpiForWindow(hwnd);
        return (int)Math.Round(ResizeGrabLogical * (dpi / 96.0));
    }

    private static int TitleBarHeightPx(IntPtr hwnd)
    {
        var dpi = Native.GetDpiForWindow(hwnd);
        return (int)Math.Round(CustomTitleBarHeightLogical * (dpi / 96.0));
    }

    private static bool HasAutoHideAppBar(IntPtr monitor, uint edge)
    {
        var data = new Native.APPBARDATA
        {
            cbSize = (uint)sizeof(Native.APPBARDATA),
            uEdge = edge,
            rc = new Native.RECT
            {
                Left = 0, Top = 0, Right = 0, Bottom = 0,
            },
        };
        // SHAppBarMessage stuffs the discovered HWND in the appbar's
        // hWnd field; non-zero means an auto-hide bar exists on that
        // edge of the same monitor as our window.
        // The rc field is the monitor rect filter - leave zeroed to
        // mean "any monitor"; we cross-check via MonitorFromWindow.
        var hwnd = Native.SHAppBarMessage(Native.ABM_GETAUTOHIDEBAREX, ref data);
        if (hwnd == IntPtr.Zero) return false;
        var barMonitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        return barMonitor == monitor;
    }

    private static IntPtr TryLoadAppIcon()
    {
        // nexus-overlay.exe lives at ...\Nexus\overlay\; nexus-service drops
        // icon.ico one directory up at ...\Nexus\icon.ico (its
        // <ApplicationIcon> CopyToOutputDirectory). Resolve at runtime instead
        // of embedding a second copy.
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
            var dir = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(dir)) return IntPtr.Zero;
            var candidates = new[]
            {
                Path.Combine(dir, "icon.ico"),
                Path.Combine(dir, "..", "icon.ico"),
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                var hIcon = Native.LoadImageW(
                    IntPtr.Zero, path, Native.IMAGE_ICON, 0, 0,
                    Native.LR_LOADFROMFILE | Native.LR_DEFAULTSIZE | Native.LR_SHARED);
                if (hIcon != IntPtr.Zero)
                {
                    Log.Info($"dashboard loaded app icon from {path}");
                    return hIcon;
                }
                Log.Error($"dashboard LoadImage({path}) returned null");
            }
            Log.Info("dashboard icon.ico not found - taskbar uses default icon");
        }
        catch (Exception ex) { Log.Error($"dashboard TryLoadAppIcon: {ex.Message}"); }
        return IntPtr.Zero;
    }

    private static void ApplyImmersiveTheme(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        // Try the modern attribute id first; fall back to the legacy id
        // on older builds where it had a different ordinal.
        var hr = Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, value, sizeof(int));
        if (hr != 0)
        {
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, value, sizeof(int));
        }
    }

    // Request the Windows 11 Mica system backdrop (DWMSBT_MAINWINDOW) behind
    // the extended frame, plus the attributes that keep it alive across
    // window-state changes. No-op on Win10 / pre-22000, where the calls just
    // return a nonzero HRESULT we ignore.
    private void ApplyMicaBackdrop()
    {
        int backdrop = Native.DWMSBT_MAINWINDOW;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, backdrop, sizeof(int));
        // Keeps the backdrop composed through the maximize animation instead
        // of flashing black for its duration.
        int hostBrush = 1;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_USE_HOSTBACKDROPBRUSH, hostBrush, sizeof(int));
        // With sheet-of-glass margins the top of the extended frame is where
        // a caption bar would go; suppress it so nothing paints through the
        // transparent WebView2.
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_CAPTION_COLOR, Native.DWMWA_COLOR_NONE, sizeof(int));
    }

    // Re-assert the full glass configuration (frame margins, then backdrop).
    // DWM drops the system backdrop on some state transitions (maximize /
    // restore / DPI moves); re-running the sequence revives it in place, so a
    // window that ever composes black recovers without being recreated.
    private void ApplyGlassFrame()
    {
        ApplyCustomFrameMargins(Hwnd);
        ApplyMicaBackdrop();
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* registry is best-effort; default to light */ }
        return false;
    }

    public void ShowAndFocus()
    {
        if (Hwnd == IntPtr.Zero) return;
        // Re-apply the last app-resolved theme in case the page pushed a new
        // one while the window was hidden, which would otherwise paint stale
        // on show.
        ApplyImmersiveTheme(Hwnd, _appDark);
        ApplyGlassFrame();
        if (Native.IsIconic(Hwnd))
        {
            Native.ShowWindow(Hwnd, Native.SW_RESTORE);
        }
        else
        {
            Native.ShowWindow(Hwnd, Native.SW_SHOW);
        }
        Native.BringWindowToTop(Hwnd);
        Native.SetForegroundWindow(Hwnd);
    }

    // Navigate the live WebView2 to a new same-origin URL (the tray "Settings"
    // deep-link on an already-open dashboard). Called on the marshaler/UI
    // thread, same thread the WebView2 lives on. If CoreWebView2 isn't ready
    // yet, redirect the pending init navigation instead.
    public void Navigate(string url)
    {
        if (_coreWebView2 == IntPtr.Zero)
        {
            _navigationUrl = url;
            return;
        }
        var hr = Wv2.Wv2_Navigate(_coreWebView2, url);
        Log.Info($"dashboard re-navigate hr=0x{hr:X8} url={url}");
    }

    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INIT_CONTROLLER:
                InitController();
                return IntPtr.Zero;

            case Native.WM_ACTIVATE:
                // DWM resets the extended frame margins on some activation
                // transitions (notably maximize / restore); re-apply the full
                // glass sequence so the backdrop survives them.
                ApplyGlassFrame();
                return null; // let DefWindowProc finish standard processing

            case Native.WM_NCCALCSIZE:
                return HandleNcCalcSize(hwnd, wParam, lParam);

            case Native.WM_NCACTIVATE:
                // Custom-frame window: DefWindowProc would paint the
                // (now non-existent) inactive-caption strip across the
                // top of the window on deactivate, leaving a 1px
                // artefact at the top edge. lParam = -1 tells the OS
                // "don't redraw the non-client region"; we still need
                // to return TRUE so the activation change itself is
                // processed normally.
                return Native.DefWindowProcW(hwnd, msg, wParam, (IntPtr)(-1));

            case Native.WM_NCHITTEST:
                return HandleNcHitTest(hwnd, msg, wParam, lParam);

            case Native.WM_NCMOUSEMOVE:
            case Native.WM_NCLBUTTONDOWN:
            case Native.WM_NCLBUTTONUP:
                // Forward to DWM for protocol completeness. With WS_SYSMENU
                // absent there are no system caption buttons, so this always
                // returns 0 today and falls through to DefWindowProc; kept so
                // DWM-owned NC interactions keep working if the style ever
                // regains them.
                if (Native.DwmDefWindowProc(hwnd, msg, wParam, lParam, out var dwmRes) != 0)
                {
                    return dwmRes;
                }
                return null;

            case Native.WM_SIZE:
                if (_controller != IntPtr.Zero)
                {
                    Native.GetClientRect(hwnd, out var rc);
                    Wv2.Ctrl_put_Bounds(_controller, rc);
                }
                int sizeKind = wParam.ToInt32();
                // Maximize / restore are the transitions DWM is known to drop
                // the system backdrop on; re-assert glass at both end states
                // (skip minimize - nothing is visible there). Gate on an
                // actual state change: WM_SIZE arrives per tick during an
                // interactive drag-resize, all with the restored kind, and
                // those repeats don't need the DWM round-trips.
                if (sizeKind != _lastSizeKind)
                {
                    _lastSizeKind = sizeKind;
                    if (sizeKind == Native.SIZE_MAXIMIZED || sizeKind == Native.SIZE_RESTORED)
                    {
                        ApplyGlassFrame();
                    }
                }
                // Persist size only when restored - skip min/maximize so
                // closing from maximized doesn't bake the maximized rect
                // as the new "normal" bounds.
                if (sizeKind == Native.SIZE_RESTORED)
                {
                    _saveOnClose = true;
                }
                return IntPtr.Zero;

            case Native.WM_MOVE:
                _saveOnClose = true;
                return IntPtr.Zero;

            case Native.WM_GETMINMAXINFO:
                if (lParam != IntPtr.Zero)
                {
                    // Custom NC handler means client == window, so the
                    // OS-tracked minimum is just the client minimum -
                    // no AdjustWindowRectEx for caption / borders here.
                    var mmi = (Native.MINMAXINFO*)lParam;
                    mmi->ptMinTrackSize.x = MinClientWidth;
                    mmi->ptMinTrackSize.y = MinClientHeight;
                }
                return IntPtr.Zero;

            case Native.WM_DPICHANGED:
                if (lParam != IntPtr.Zero)
                {
                    var sug = *(Native.RECT*)lParam;
                    Native.SetWindowPos(hwnd, IntPtr.Zero,
                        sug.Left, sug.Top, sug.Width, sug.Height,
                        Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
                    // Re-assert glass after the cross-monitor move; DPI
                    // transitions are another spot DWM can shed the backdrop.
                    ApplyGlassFrame();
                }
                return IntPtr.Zero;

            case Native.WM_DWMCOLORIZATIONCOLORCHANGED:
                // System accent / colorization changed - push the new accent.
                PostSystemAccent();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                // Fully tear down on close so the next open does a fresh
                // WebView2 init + navigation - picks up any newly-deployed
                // wwwroot. Saving bounds first; Program clears the
                // singleton + arms idle-exit inside OnDashboardClosed.
                if (_saveOnClose)
                {
                    SaveBounds();
                    _saveOnClose = false;
                }
                Program.OnDashboardClosed();
                Native.DestroyWindow(hwnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Dispose();
                return IntPtr.Zero;
        }
        return null;
    }

    // ===================== Custom title bar =====================

    private static IntPtr? HandleNcCalcSize(IntPtr hwnd, IntPtr wParam, IntPtr lParam)
    {
        // wParam == FALSE: lParam is a RECT, leave default behaviour.
        if (wParam == IntPtr.Zero) return null;

        // wParam == TRUE: NCCALCSIZE_PARAMS, rgrc[0] is the proposed
        // client rect. The default WM_NCCALCSIZE insets rgrc[0].top by the
        // caption height (~30px) - skipping THAT here is what visually
        // removes the title bar. We still inset side / bottom / a tiny
        // top sliver so WS_THICKFRAME has the non-client area it needs to
        // dispatch WM_NCHITTEST for the resize cursor - without that
        // sliver the top-left / top-right corners are unreachable
        // because the WebView2 child HWND captures the click first.
        unsafe
        {
            // Default: client area equals the full window rect - WebView2
            // sits flush against every edge with zero padding. Mouse
            // events at the edges land on the WebView2 child window, and
            // the React app forwards them to this WM_NCHITTEST via the
            // app-region: drag strips along all four borders (enabled
            // by ICoreWebView2Settings9.IsNonClientRegionSupportEnabled).
            var p = (Native.NCCALCSIZE_PARAMS*)lParam;

            // Maximized windows are an exception: WS_THICKFRAME causes
            // the OS to push the window rect a few pixels off-screen so
            // the resize border sits at the visible monitor edge. Without
            // re-insetting here, the top/sides of the maximized window
            // get clipped beyond the work area.
            if (Native.IsZoomed(hwnd))
            {
                int frameX = Native.GetSystemMetrics(Native.SM_CXFRAME);
                int frameY = Native.GetSystemMetrics(Native.SM_CYFRAME);
                int padding = Native.GetSystemMetrics(Native.SM_CXPADDEDBORDER);
                p->rgrc0.Top += frameY + padding;
                p->rgrc0.Left += frameX + padding;
                p->rgrc0.Right -= frameX + padding;
                p->rgrc0.Bottom -= frameY + padding;

                // Auto-hide-taskbar guard. When the maximized window
                // covers the work area edge-to-edge, the OS no longer
                // detects mouse-near-edge to pop the taskbar. Subtract
                // 1 px on whichever edge actually has an auto-hide
                // appbar so the reveal trigger keeps firing.
                var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
                if (HasAutoHideAppBar(monitor, Native.ABE_BOTTOM)) p->rgrc0.Bottom -= 1;
                if (HasAutoHideAppBar(monitor, Native.ABE_TOP)) p->rgrc0.Top += 1;
                if (HasAutoHideAppBar(monitor, Native.ABE_LEFT)) p->rgrc0.Left += 1;
                if (HasAutoHideAppBar(monitor, Native.ABE_RIGHT)) p->rgrc0.Right -= 1;
            }
        }
        return IntPtr.Zero; // WVR_VALIDRECTS
    }

    private static IntPtr? HandleNcHitTest(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Step 1: give DWM first claim on the hit. With WS_SYSMENU absent
        // there are no system caption buttons, so this returns 0 today; the
        // web renders its own min / max / close inside the WebView2. Kept
        // for protocol completeness should the style ever regain them.
        if (Native.DwmDefWindowProc(hwnd, msg, wParam, lParam, out var dwmResult) != 0)
        {
            return dwmResult;
        }

        // Step 2: translate cursor position (screen coords in lParam) to
        // WINDOW-local coords and hit-test the resize border + drag strip.
        // Window-local (not client-local) because the non-client area
        // outside the client rect has negative client coords, so
        // ScreenToClient would read corners as pt < 0 and fall through to
        // HTCLIENT (no resize cursor).
        int lp = (int)(lParam.ToInt64() & 0xFFFFFFFF);
        int screenX = (short)(lp & 0xFFFF);
        int screenY = (short)((lp >> 16) & 0xFFFF);
        Native.GetWindowRect(hwnd, out var wrc);
        int px = screenX - wrc.Left;
        int py = screenY - wrc.Top;
        int W = wrc.Width;
        int H = wrc.Height;

        int side = ResizeBorderThickness(hwnd);
        int titleH = TitleBarHeightPx(hwnd);
        bool maximized = Native.IsZoomed(hwnd);

        // Forwarded events come from `app-region: drag` regions: a top drag
        // strip and a thin strip along each edge. Bucket by position: corner
        // / edge strips give resize, the top strip gives drag, anything else
        // is HTCLIENT. Resize is suppressed while maximized (the OS doesn't
        // resize a maximized window).
        if (!maximized)
        {
            bool top = py >= 0 && py < side;
            bool bottom = py >= H - side && py < H;
            bool left = px >= 0 && px < side;
            bool right = px >= W - side && px < W;

            if (top && left) return new IntPtr(Native.HTTOPLEFT);
            if (top && right) return new IntPtr(Native.HTTOPRIGHT);
            if (bottom && left) return new IntPtr(Native.HTBOTTOMLEFT);
            if (bottom && right) return new IntPtr(Native.HTBOTTOMRIGHT);
            if (top) return new IntPtr(Native.HTTOP);
            if (bottom) return new IntPtr(Native.HTBOTTOM);
            if (left) return new IntPtr(Native.HTLEFT);
            if (right) return new IntPtr(Native.HTRIGHT);
        }

        if (py >= 0 && py < titleH)
        {
            return new IntPtr(Native.HTCAPTION);
        }

        return new IntPtr(Native.HTCLIENT);
    }

    // ===================== WebView2 init flow =====================

    private void StartWebView2Init()
    {
        // Same user-data-dir as OverlayWindow so Chromium reuses the existing
        // browser/GPU/utility processes instead of spawning a second tree.
        var userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Nexus", "DesktopWebView2");
        try { Directory.CreateDirectory(userDataDir); } catch { /* best-effort */ }

        _envCreatedHandler = WebView2Callbacks.CreateEnvCreatedHandler(&OnEnvCreatedStatic);
        fixed (char* udf = userDataDir)
        {
            var hr = WebView2Native.CreateCoreWebView2EnvironmentWithOptions(
                null, udf, IntPtr.Zero, _envCreatedHandler);
            Log.Info($"dashboard CreateCoreWebView2Env hr=0x{hr:X8}");
            if (WebView2Native.Failed(hr))
            {
                Log.Error($"dashboard env init failed hr=0x{hr:X8}");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnEnvCreatedStatic(IntPtr self, int errorCode, IntPtr env)
    {
        var owner = FindByEnvHandler(self);
        owner?.OnEnvCreated(errorCode, env);
        return WebView2Native.S_OK;
    }

    private void OnEnvCreated(int errorCode, IntPtr env)
    {
        Log.Info($"dashboard env created err=0x{errorCode:X8} env=0x{env:X}");
        if (WebView2Native.Failed(errorCode) || env == IntPtr.Zero) return;
        Wv2.AddRef(env);
        _env = env;
        Native.PostMessageW(Hwnd, WM_INIT_CONTROLLER, IntPtr.Zero, IntPtr.Zero);
    }

    private void InitController()
    {
        if (_env == IntPtr.Zero) { Log.Error("dashboard InitController: env null"); return; }
        _ctrlCreatedHandler = WebView2Callbacks.CreateControllerCreatedHandler(&OnControllerCreatedStatic);
        var hr = Wv2.Env_CreateCoreWebView2Controller(_env, Hwnd, _ctrlCreatedHandler);
        Log.Info($"dashboard CreateCoreWebView2Controller hr=0x{hr:X8}");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnControllerCreatedStatic(IntPtr self, int errorCode, IntPtr controller)
    {
        var owner = FindByCtrlHandler(self);
        owner?.OnControllerCreated(errorCode, controller);
        return WebView2Native.S_OK;
    }

    private void OnControllerCreated(int errorCode, IntPtr controller)
    {
        Log.Info($"dashboard ctrl created err=0x{errorCode:X8} ctrl=0x{controller:X}");
        if (WebView2Native.Failed(errorCode) || controller == IntPtr.Zero)
        {
            Log.Error($"dashboard controller creation failed hr=0x{errorCode:X8}");
            return;
        }
        Wv2.AddRef(controller);
        _controller = controller;

        // Transparent default backdrop so the DWM Mica system backdrop shows
        // through wherever the page paints transparent.
        _controller2 = Wv2.QueryInterface(controller, Wv2.IID_ICoreWebView2Controller2);
        if (_controller2 != IntPtr.Zero)
        {
            Wv2.Ctrl2_put_DefaultBackgroundColor(_controller2, DefaultBgTransparent);
        }

        Native.GetClientRect(Hwnd, out var rc);
        Wv2.Ctrl_put_Bounds(_controller, rc);
        Wv2.Ctrl_put_IsVisible(_controller, true);

        if (WebView2Native.Failed(Wv2.Ctrl_get_CoreWebView2(_controller, out _coreWebView2)) || _coreWebView2 == IntPtr.Zero)
        {
            Log.Error("dashboard get_CoreWebView2 failed");
            return;
        }

        if (WebView2Native.Succeeded(Wv2.Wv2_get_Settings(_coreWebView2, out var settings)) && settings != IntPtr.Zero)
        {
            Wv2.Settings_put_AreDefaultContextMenusEnabled(settings, true);
            Wv2.Settings_put_AreDevToolsEnabled(settings, true);
            Wv2.Settings_put_IsStatusBarEnabled(settings, true);
            Wv2.Settings_put_IsZoomControlEnabled(settings, true);

            // WebView2 defaults IsPasswordAutosaveEnabled to false; the
            // dashboard has real sign-in forms (account login), so opt in.
            var settings4 = Wv2.QueryInterface(settings, WebView2Native.IID_ICoreWebView2Settings4);
            if (settings4 != IntPtr.Zero)
            {
                Wv2.Settings4_put_IsPasswordAutosaveEnabled(settings4, true);
                Wv2.Settings4_put_IsGeneralAutofillEnabled(settings4, true);
                Wv2.Release(settings4);
            }
            else
            {
                Log.Info("dashboard ICoreWebView2Settings4 not available - password autosave disabled");
            }

            // ICoreWebView2Settings9 is added at SDK 1.0.2420.47; older
            // hosts return E_NOINTERFACE on QI and we silently skip - the
            // drag region just won't work there and the user falls back to
            // dragging via the system maximize / restore double-click.
            var settings9 = Wv2.QueryInterface(settings, WebView2Native.IID_ICoreWebView2Settings9);
            if (settings9 != IntPtr.Zero)
            {
                var ncHr = Wv2.Settings9_put_IsNonClientRegionSupportEnabled(settings9, true);
                if (WebView2Native.Failed(ncHr))
                {
                    Log.Error($"dashboard Settings9_put_IsNonClientRegionSupportEnabled hr=0x{ncHr:X8}");
                }
                Wv2.Release(settings9);
            }
            else
            {
                Log.Info("dashboard ICoreWebView2Settings9 not available - drag region disabled");
            }

            Wv2.Release(settings);
        }

        // Feature parity with Edge --app: external links go to the OS
        // default browser, popup windows open externally, all WebAPI
        // permission prompts auto-deny.
        _navStartingHandler = WebView2Callbacks.CreateNavigationStartingHandler(&OnNavigationStartingStatic);
        var navHr = Wv2.Wv2_add_NavigationStarting(_coreWebView2, _navStartingHandler, out _navStartingToken);
        if (WebView2Native.Failed(navHr)) Log.Error($"dashboard add_NavigationStarting failed hr=0x{navHr:X8}");

        _newWindowHandler = WebView2Callbacks.CreateNewWindowRequestedHandler(&OnNewWindowRequestedStatic);
        var nwHr = Wv2.Wv2_add_NewWindowRequested(_coreWebView2, _newWindowHandler, out _newWindowToken);
        if (WebView2Native.Failed(nwHr)) Log.Error($"dashboard add_NewWindowRequested failed hr=0x{nwHr:X8}");

        _permissionHandler = WebView2Callbacks.CreatePermissionRequestedHandler(&OnPermissionRequestedStatic);
        var permHr = Wv2.Wv2_add_PermissionRequested(_coreWebView2, _permissionHandler, out _permissionToken);
        if (WebView2Native.Failed(permHr)) Log.Error($"dashboard add_PermissionRequested failed hr=0x{permHr:X8}");

        // Window-action IPC: the React app posts {type:'window-action',action:...}
        // strings via window.chrome.webview.postMessage(...) when the user
        // clicks our custom title bar's min / max / close buttons. We dispatch
        // them to the matching Win32 calls on the dashboard HWND.
        _webMessageHandler = WebView2Callbacks.CreateWebMessageReceivedHandler(&OnWebMessageReceivedStatic);
        var wmHr = Wv2.Wv2_add_WebMessageReceived(_coreWebView2, _webMessageHandler, out _webMessageToken);
        if (WebView2Native.Failed(wmHr)) Log.Error($"dashboard add_WebMessageReceived failed hr=0x{wmHr:X8}");

        // Flag the page so its CSS / React tree knows it's running inside the
        // Nexus Windows shell. The React app uses this to render the custom
        // caption buttons + reserve the top drag strip; without the flag the
        // page renders its normal layout (browser / macOS / phone shells).
        // Runs before every document creation, so survives navigations.
        var injectHr = Wv2.Wv2_AddScriptToExecuteOnDocumentCreated(_coreWebView2,
            "Object.defineProperty(window,'nexusShellPlatform',{value:'windows-app',writable:false,configurable:false});");
        if (WebView2Native.Failed(injectHr)) Log.Error($"dashboard AddScriptToExecuteOnDocumentCreated hr=0x{injectHr:X8}");

        var hr = Wv2.Wv2_Navigate(_coreWebView2, _navigationUrl);
        Log.Info($"dashboard Navigate hr=0x{hr:X8} url={_navigationUrl}");

        // Reveal the window only after the controller is attached so
        // there's no flash of pre-WebView2 caption-bar-only frame.
        Native.ShowWindow(Hwnd, Native.SW_SHOWNORMAL);
        Native.BringWindowToTop(Hwnd);
        Native.SetForegroundWindow(Hwnd);
    }

    // ===================== Feature-parity event handlers =====================

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNavigationStartingStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNavStartingHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.NavStartingArgs_get_Uri(args, out var uri);
            if (!IsLocalDashboardUri(uri))
            {
                Wv2.NavStartingArgs_put_Cancel(args, true);
                ShellOpenExternal(uri);
                Log.Info($"dashboard external nav redirected to default browser: {uri}");
            }
        }
        catch (Exception ex) { Log.Error($"dashboard NavStarting: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnNewWindowRequestedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByNewWindowHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.NewWindowArgs_get_Uri(args, out var uri);
            Wv2.NewWindowArgs_put_Handled(args, true);
            ShellOpenExternal(uri);
            Log.Info($"dashboard target=_blank routed to default browser: {uri}");
        }
        catch (Exception ex) { Log.Error($"dashboard NewWindow: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnPermissionRequestedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByPermissionHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            Wv2.PermissionArgs_get_PermissionKind(args, out var kind);
            Wv2.PermissionArgs_put_State(args, PermissionStateDeny);
            Log.Info($"dashboard permission denied kind={kind}");
        }
        catch (Exception ex) { Log.Error($"dashboard Permission: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnWebMessageReceivedStatic(IntPtr self, IntPtr sender, IntPtr args)
    {
        var owner = FindByWebMessageHandler(self);
        if (owner is null || args == IntPtr.Zero) return WebView2Native.S_OK;
        try
        {
            // Strings sent via postMessage('...') land here as plain text.
            // We use the string form (not JSON) because the message format is
            // a fixed-set sentinel and a single TryGet call avoids a JSON
            // dependency in this hot path.
            if (WebView2Native.Failed(Wv2.WebMsgArgs_TryGetWebMessageAsString(args, out var text)) || text is null)
                return WebView2Native.S_OK;
            // Gallery drop bridge: the page can't see dropped files' disk
            // paths (web sandbox), but the host can - it reads them off the
            // message's AdditionalObjects and posts them straight back; the
            // page then registers them as sources over its own authed API.
            // Must happen here while `args` is alive, not in HandleWindowAction.
            if (text == "nexus:gallery-drop")
            {
                owner.HandleGalleryDrop(args);
                return WebView2Native.S_OK;
            }
            if (text == "nexus:request-system-accent")
            {
                owner.PostSystemAccent();
                return WebView2Native.S_OK;
            }
            // The page pushes its resolved theme so the immersive/Mica frame
            // follows the in-app theme instead of the Windows OS theme.
            if (text == "nexus:theme-dark" || text == "nexus:theme-light")
            {
                owner._appDark = text == "nexus:theme-dark";
                ApplyImmersiveTheme(owner.Hwnd, owner._appDark);
                owner.ApplyMicaBackdrop();
                return WebView2Native.S_OK;
            }
            owner.HandleWindowAction(text);
        }
        catch (Exception ex) { Log.Error($"dashboard WebMessage: {ex.Message}"); }
        return WebView2Native.S_OK;
    }

    private void HandleGalleryDrop(IntPtr args)
    {
        var paths = Wv2.WebMsgArgs_GetAdditionalFilePaths(args);
        Log.Info($"dashboard gallery-drop: {paths.Count} path(s) from AdditionalObjects");
        if (_coreWebView2 == IntPtr.Zero) return;
        var json = JsonSerializer.Serialize(
            new GalleryDropPaths { Type = "nexus:gallery-drop-paths", Paths = paths },
            DashboardBoundsJson.Default.GalleryDropPaths);
        var hr = Wv2.Wv2_PostWebMessageAsJson(_coreWebView2, json);
        if (WebView2Native.Failed(hr)) Log.Error($"dashboard gallery-drop post hr=0x{hr:X8}");
    }

    // Reads the user's Windows accent colour and pushes it to the page (for the
    // "System" accent option). Sent on init/request + on colour change.
    private void PostSystemAccent()
    {
        if (_coreWebView2 == IntPtr.Zero) return;
        var hex = ReadSystemAccentHex();
        if (hex is null) return;
        var json = JsonSerializer.Serialize(
            new SystemAccent { Type = "nexus:system-accent", Hex = hex }, DashboardBoundsJson.Default.SystemAccent);
        Wv2.Wv2_PostWebMessageAsJson(_coreWebView2, json);
    }

    // HKCU\Software\Microsoft\Windows\DWM\AccentColor is a REG_DWORD in 0xAABBGGRR
    // (ABGR) order; map it to #RRGGBB. Overlay runs as the user, so HKCU is theirs.
    private static string? ReadSystemAccentHex()
    {
        uint data = 0, type = 0, cb = sizeof(uint);
        int rc = Native.RegGetValueW(Native.HKEY_CURRENT_USER, @"Software\Microsoft\Windows\DWM",
            "AccentColor", Native.RRF_RT_REG_DWORD, out type, out data, ref cb);
        if (rc != 0) return null;
        int r = (int)(data & 0xFF);
        int g = (int)((data >> 8) & 0xFF);
        int b = (int)((data >> 16) & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private void HandleWindowAction(string action)
    {
        // Sentinels match NEXUS_WINDOW_ACTIONS / NEXUS_RESIZE_EDGES in nexus-web
        // (windowActions.ts). Keep this switch in lockstep.
        switch (action)
        {
            case "nexus:window-minimize":
                Native.ShowWindow(Hwnd, Native.SW_SHOWMINIMIZED);
                break;
            case "nexus:window-toggle-maximize":
                // Single button on the page: ask the OS for current state and
                // flip. The corresponding restore icon swap is signalled back
                // to the page through DOM resize (the React app subscribes to
                // window.matchMedia + ResizeObserver to update its glyph).
                if (Native.IsZoomed(Hwnd))
                    Native.ShowWindow(Hwnd, Native.SW_RESTORE);
                else
                    Native.ShowWindow(Hwnd, Native.SW_SHOWMAXIMIZED);
                break;
            case "nexus:window-close":
                Native.PostMessageW(Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            case "nexus:resize-left":         BeginNcDrag(Native.HTLEFT); break;
            case "nexus:resize-right":        BeginNcDrag(Native.HTRIGHT); break;
            case "nexus:resize-top":          BeginNcDrag(Native.HTTOP); break;
            case "nexus:resize-bottom":       BeginNcDrag(Native.HTBOTTOM); break;
            case "nexus:resize-top-left":     BeginNcDrag(Native.HTTOPLEFT); break;
            case "nexus:resize-top-right":    BeginNcDrag(Native.HTTOPRIGHT); break;
            case "nexus:resize-bottom-left":  BeginNcDrag(Native.HTBOTTOMLEFT); break;
            case "nexus:resize-bottom-right": BeginNcDrag(Native.HTBOTTOMRIGHT); break;
            // Title-bar drag. HTCAPTION runs the OS move loop (Aero Snap incl.).
            // The web bar uses this instead of `app-region: drag` so it stays a
            // client region and doesn't black-flicker on resize over Mica.
            case "nexus:window-drag":         BeginNcDrag(Native.HTCAPTION); break;
            default:
                // Other messages may flow through here for valid IPC; do
                // not log them as errors.
                break;
        }
    }

    // Hand a web-initiated non-client drag to the OS. hitCode = HTCAPTION runs
    // the window move loop; the HT{LEFT,RIGHT,...} codes run the resize tracker.
    private void BeginNcDrag(int hitCode)
    {
        // The React strip/bar caught the mousedown - WebView2 has captured
        // input. Release it so the OS native drag loop can attach.
        Native.ReleaseCapture();
        // Post WM_NCLBUTTONDOWN with the HT code; the OS reads GetCursorPos for
        // the anchor and runs its own move / resize tracker.
        Native.PostMessageW(Hwnd, Native.WM_NCLBUTTONDOWN, (IntPtr)hitCode, IntPtr.Zero);
    }

    private static bool IsLocalDashboardUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return false;
        if (uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase)) return true;
        return uri.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("ws://localhost:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("ws://127.0.0.1:", StringComparison.OrdinalIgnoreCase);
    }

    private static void ShellOpenExternal(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        try
        {
            Native.ShellExecuteW(IntPtr.Zero, "open", uri, null, null, Native.SW_SHOW);
        }
        catch (Exception ex) { Log.Error($"ShellExecute external: {ex.Message}"); }
    }

    // ===================== Bounds persistence =====================

    private static string BoundsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nexus", "dashboard-bounds.json");

    private (int x, int y, int w, int h) ResolveInitialBounds()
    {
        var saved = LoadSavedBounds();
        if (saved is not null) return saved.Value;

        // Default: center a DefaultClientWidth x DefaultClientHeight client
        // area on the primary monitor. AdjustWindowRectEx inflates to the
        // outer-frame size so the visible client matches what we asked for.
        var rc = new Native.RECT
        {
            Left = 0, Top = 0,
            Right = DefaultClientWidth, Bottom = DefaultClientHeight,
        };
        Native.AdjustWindowRectEx(ref rc, Native.WS_OVERLAPPEDWINDOW, false, 0u);
        var w = rc.Width;
        var h = rc.Height;
        var screenW = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        var screenH = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        var x = (screenW - w) / 2;
        var y = (screenH - h) / 2;
        return (x, y, w, h);
    }

    private static (int x, int y, int w, int h)? LoadSavedBounds()
    {
        try
        {
            var path = BoundsFilePath();
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var doc = JsonSerializer.Deserialize(stream, DashboardBoundsJson.Default.SavedBounds);
            if (doc is null) return null;
            // The stored bounds are outer-frame size. Refuse anything that
            // would land us under the documented client-area minimum,
            // including a corrupted/zeroed file.
            var minOuter = new Native.RECT { Left = 0, Top = 0, Right = MinClientWidth, Bottom = MinClientHeight };
            Native.AdjustWindowRectEx(ref minOuter, Native.WS_OVERLAPPEDWINDOW, false, 0u);
            if (doc.W < minOuter.Width || doc.H < minOuter.Height) return null;
            return (doc.X, doc.Y, doc.W, doc.H);
        }
        catch { return null; }
    }

    private void SaveBounds()
    {
        if (Hwnd == IntPtr.Zero) return;
        try
        {
            var wp = new Native.WINDOWPLACEMENT();
            wp.length = (uint)sizeof(Native.WINDOWPLACEMENT);
            if (!Native.GetWindowPlacement(Hwnd, ref wp)) return;
            // Always save the rcNormalPosition (the restored bounds) so
            // a maximized window doesn't restore to a maximized rect that
            // covers the taskbar after a Win32-only restore.
            var rc = wp.rcNormalPosition;
            var path = BoundsFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var data = new SavedBounds
            {
                X = rc.Left, Y = rc.Top,
                W = rc.Width, H = rc.Height,
            };
            using var fs = File.Create(path);
            JsonSerializer.Serialize(fs, data, DashboardBoundsJson.Default.SavedBounds);
        }
        catch (Exception ex) { Log.Error($"dashboard SaveBounds: {ex.Message}"); }
    }

    // ===================== Callback dispatch =====================

    private static DashboardWindow? FindByEnvHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._envCreatedHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByCtrlHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._ctrlCreatedHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByNavStartingHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._navStartingHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByNewWindowHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._newWindowHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByPermissionHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._permissionHandler == handler) return kv.Value;
        return null;
    }

    private static DashboardWindow? FindByWebMessageHandler(IntPtr handler)
    {
        foreach (var kv in _instances)
            if (kv.Value._webMessageHandler == handler) return kv.Value;
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _instances.TryRemove(_instanceId, out _);
        try
        {
            // Revoke event subscriptions on the inner ICoreWebView2 before
            // closing the controller. Without this, a queued event firing
            // after Ctrl_Close would re-enter a handler whose backing
            // object we have already freed via Release below.
            if (_coreWebView2 != IntPtr.Zero)
            {
                if (_navStartingToken != 0) { Wv2.Wv2_remove_NavigationStarting(_coreWebView2, _navStartingToken); _navStartingToken = 0; }
                if (_newWindowToken != 0) { Wv2.Wv2_remove_NewWindowRequested(_coreWebView2, _newWindowToken); _newWindowToken = 0; }
                if (_permissionToken != 0) { Wv2.Wv2_remove_PermissionRequested(_coreWebView2, _permissionToken); _permissionToken = 0; }
                if (_webMessageToken != 0) { Wv2.Wv2_remove_WebMessageReceived(_coreWebView2, _webMessageToken); _webMessageToken = 0; }
            }
            if (_controller != IntPtr.Zero)
            {
                Wv2.Ctrl_Close(_controller);
                Wv2.Release(_controller);
                _controller = IntPtr.Zero;
            }
            if (_coreWebView2 != IntPtr.Zero) { Wv2.Release(_coreWebView2); _coreWebView2 = IntPtr.Zero; }
            if (_controller2 != IntPtr.Zero) { Wv2.Release(_controller2); _controller2 = IntPtr.Zero; }
            if (_env != IntPtr.Zero) { Wv2.Release(_env); _env = IntPtr.Zero; }
            // Release our local refs on the callback objects. The host
            // also AddRef'd them when we passed them into the add_* / env
            // creation calls; the matching Release was paired with the
            // remove_* above (for events) or fires automatically when the
            // host tears down (for env/controller-created handlers).
            if (_envCreatedHandler != IntPtr.Zero) { Wv2.Release(_envCreatedHandler); _envCreatedHandler = IntPtr.Zero; }
            if (_ctrlCreatedHandler != IntPtr.Zero) { Wv2.Release(_ctrlCreatedHandler); _ctrlCreatedHandler = IntPtr.Zero; }
            if (_navStartingHandler != IntPtr.Zero) { Wv2.Release(_navStartingHandler); _navStartingHandler = IntPtr.Zero; }
            if (_newWindowHandler != IntPtr.Zero) { Wv2.Release(_newWindowHandler); _newWindowHandler = IntPtr.Zero; }
            if (_permissionHandler != IntPtr.Zero) { Wv2.Release(_permissionHandler); _permissionHandler = IntPtr.Zero; }
            if (_webMessageHandler != IntPtr.Zero) { Wv2.Release(_webMessageHandler); _webMessageHandler = IntPtr.Zero; }
        }
        catch (Exception ex) { Log.Error($"dashboard dispose: {ex.Message}"); }
        if (Hwnd != IntPtr.Zero)
        {
            Win32Window.Unregister(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }
}

internal sealed class SavedBounds
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

/// <summary>Host → page reply carrying dropped files' real disk paths.</summary>
internal sealed class GalleryDropPaths
{
    public string Type { get; set; } = "";
    public System.Collections.Generic.List<string> Paths { get; set; } = new();
}

/// <summary>Host → page: the OS accent colour as #RRGGBB.</summary>
internal sealed class SystemAccent
{
    public string Type { get; set; } = "";
    public string Hex { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedBounds))]
[System.Text.Json.Serialization.JsonSerializable(typeof(GalleryDropPaths))]
[System.Text.Json.Serialization.JsonSerializable(typeof(SystemAccent))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal partial class DashboardBoundsJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
