using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Win32;

/// <summary>
/// Slim Win32 P/Invoke surface used by the desktop overlay. AOT-safe:
/// blittable types only, no managed-delegate marshalling (callbacks go
/// through [UnmanagedCallersOnly] statics + Win32 dispatch tables), no
/// CharSet=Auto fallback paths.
/// </summary>
internal static unsafe class Native
{
    // Extended styles.
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    // Window styles.
    public const uint WS_POPUP = 0x80000000u;
    public const uint WS_VISIBLE = 0x10000000u;
    public const uint WS_CAPTION = 0x00C00000u;
    public const uint WS_SYSMENU = 0x00080000u;
    public const uint WS_THICKFRAME = 0x00040000u;
    public const uint WS_MINIMIZEBOX = 0x00020000u;
    public const uint WS_MAXIMIZEBOX = 0x00010000u;
    public const uint WS_CLIPCHILDREN = 0x02000000u;
    public const uint WS_CLIPSIBLINGS = 0x04000000u;
    public const uint WS_OVERLAPPEDWINDOW =
        WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;

    // ShowWindow.
    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_RESTORE = 9;

    // GetSysColor / hbrBackground index for "Window" - HBRUSH for a
    // window class is `(IntPtr)(COLOR_WINDOW + 1)` per the WNDCLASSEXW
    // documented quirk.
    public const int COLOR_WINDOW = 5;

    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOREDRAW = 0x0008;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);
    public static readonly IntPtr HWND_TOP = new(0);
    public static readonly IntPtr HWND_BOTTOM = new(1);

    public const int RGN_OR = 2;

    // Window messages.
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_MOVE = 0x0003;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_ACTIVATE = 0x0006;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_COPYDATA = 0x004A;
    public const uint WM_NCCALCSIZE = 0x0083;

    /// <summary>WM_COPYDATA payload; dwData carries the sender's registered message id.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }
    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCACTIVATE = 0x0086;
    public const uint WM_NCMOUSEMOVE = 0x00A0;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_NCLBUTTONUP = 0x00A2;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_DPICHANGED = 0x02E0;
    public const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const uint WM_USER = 0x0400;

    // WM_NCHITTEST return codes.
    public const int HTERROR = -2;
    public const int HTTRANSPARENT = -1;
    public const int HTNOWHERE = 0;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTSYSMENU = 3;
    public const int HTGROWBOX = 4;
    public const int HTMENU = 5;
    public const int HTHSCROLL = 6;
    public const int HTVSCROLL = 7;
    public const int HTMINBUTTON = 8;
    public const int HTMAXBUTTON = 9;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;
    public const int HTBORDER = 18;
    public const int HTCLOSE = 20;

    // GetWindowLong / SetWindowLong indices for the standard window data.
    public const int GWL_STYLE = -16;
    public const int GWLP_HINSTANCE = -6;

    // WM_SIZE wParam values.
    public const int SIZE_RESTORED = 0;
    public const int SIZE_MINIMIZED = 1;
    public const int SIZE_MAXIMIZED = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0;
        public RECT rgrc1;
        public RECT rgrc2;
        public IntPtr lppos; // WINDOWPOS*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    // SystemMetrics indices.
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_CYCAPTION = 4;
    public const int SM_CXFRAME = 32;
    public const int SM_CYFRAME = 33;
    public const int SM_CXPADDEDBORDER = 92;

    // DPI awareness contexts (passed as IntPtr, negative magic constants).
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // COM apartment flags.
    public const uint COINIT_APARTMENTTHREADED = 0x2;

    // ====================== Window class / message loop =====================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    // LoadImageW with IMAGE_ICON + LR_LOADFROMFILE pulls an icon out of an
    // .ico file at runtime - used to give the dashboard window its taskbar
    // icon without needing the icon embedded as a resource in nexus-overlay.exe.
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

    public const uint IMAGE_ICON = 1;
    public const uint LR_LOADFROMFILE = 0x00000010;
    public const uint LR_DEFAULTSIZE = 0x00000040;
    public const uint LR_SHARED = 0x00008000;

    // WM_SETICON: associate a small (taskbar) or large (Alt+Tab) icon with a window.
    public const uint WM_SETICON = 0x0080;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AdjustWindowRectEx(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    public const uint MB_OK = 0x00000000;
    public const uint MB_YESNO = 0x00000004;
    public const uint MB_ICONWARNING = 0x00000030;
    public const uint MB_SETFOREGROUND = 0x00010000;
    public const uint MB_TOPMOST = 0x00040000;
    public const int IDYES = 6;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string? lpOperation, string lpFile,
        string? lpParameters, string? lpDirectory, int nShowCmd);

    // DWM attribute that paints the title bar in the user's chosen
    // light/dark theme. The ordinal moved between Windows builds, so
    // try the current one first and fall back to the older one.
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    // System backdrop (Windows 11 22000+). DWMWA_SYSTEMBACKDROP_TYPE selects
    // the DWM-rendered material behind the extended frame; the call returns a
    // nonzero HRESULT (ignored) on Win10 / pre-22000 where it's a no-op.
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_AUTO = 0;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;       // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
    public const int DWMSBT_TABBEDWINDOW = 4;     // Mica Alt (tabbed)

    // Hint that the app paints transparent regions over the backdrop; keeps
    // the backdrop composed through the maximize animation (Win11 22H2+,
    // nonzero HRESULT ignored on older builds).
    public const int DWMWA_USE_HOSTBACKDROPBRUSH = 17;

    // Caption colour override. DWMWA_COLOR_NONE stops DWM painting any
    // caption bar into the extended frame of a custom-frame window.
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, in int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, in MARGINS pMarInset);

    // Returns BOOL (nonzero = handled). plResult receives the result that
    // should be returned from the WNDPROC when handled. Used to forward
    // WM_NCHITTEST / WM_NCMOUSE* / WM_NCLBUTTON* to DWM so the caption
    // buttons get hover-paint and accept clicks.
    [DllImport("dwmapi.dll")]
    public static extern int DwmDefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ReleaseCapture();

    // Auto-hide-taskbar detection. When the maximized window covers the
    // monitor's work area edge-to-edge, the OS needs at least 1 px of
    // window space along the edge with an auto-hide appbar so the bar's
    // reveal trigger still fires. SHAppBarMessage(ABM_GETAUTOHIDEBAREX)
    // is the canonical query.
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public const uint ABM_GETAUTOHIDEBAREX = 0x0000000B;
    public const uint ABE_LEFT = 0;
    public const uint ABE_TOP = 1;
    public const uint ABE_RIGHT = 2;
    public const uint ABE_BOTTOM = 3;

    [DllImport("shell32.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int MDT_EFFECTIVE_DPI = 0;

    // Reads a single REG_DWORD. Used to read the user's accent colour from
    // HKCU\Software\Microsoft\Windows\DWM\AccentColor (overlay runs as the user).
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegGetValueW(IntPtr hkey, string lpSubKey, string lpValue,
        uint dwFlags, out uint pdwType, out uint pvData, ref uint pcbData);
    public static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)0x80000001L);
    public const uint RRF_RT_REG_DWORD = 0x00000010;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    // ====================== Monitor enumeration =====================

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoNative
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        // szDevice is the GDI device name (\\.\DISPLAYn) used by
        // EnumDisplayDevicesW to resolve EDID hardware info per monitor.
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, RECT*, IntPtr, int> lpfnEnum,
        IntPtr dwData);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfoNative lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string lpszDeviceName, int iModeNum, byte* lpDevMode);

    /// <summary>Current refresh rate of a GDI display (\\.\DISPLAYn), or 0 when unknown.</summary>
    public static int GetDisplayRefreshHz(string deviceName)
    {
        // DEVMODEW layout (wingdi.h): the struct size, and the offsets of dmSize and dmDisplayFrequency.
        const int DevModeSize = 220, SizeOffset = 68, FrequencyOffset = 184, EnumCurrentSettings = -1;
        var devMode = stackalloc byte[DevModeSize];
        new Span<byte>(devMode, DevModeSize).Clear();
        *(ushort*)(devMode + SizeOffset) = DevModeSize;
        if (string.IsNullOrEmpty(deviceName) || !EnumDisplaySettingsW(deviceName, EnumCurrentSettings, devMode)) return 0;
        // 0 and 1 mean the hardware default rate, which the API does not resolve.
        var hz = *(int*)(devMode + FrequencyOffset);
        return hz > 1 ? hz : 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EnumWindows(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> lpEnumFunc,
        IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassNameW(IntPtr hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    // GetAncestor flags. GA_ROOT walks the parent chain to the top-level
    // window (returns the window itself when it is already top-level).
    public const uint GA_ROOT = 2;

    // ====================== WinEvent hook (foreign-window watch) =====================

    // Shell/accessibility event IDs we watch to know when a window appears
    // on, or is dragged onto, a monitor we want kept clear.
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
    public const uint EVENT_OBJECT_SHOW = 0x8002;

    // dwFlags for SetWinEventHook. OUTOFCONTEXT delivers callbacks on the
    // registering thread via its message loop (no DLL injection); SKIPOWNPROCESS
    // suppresses events originating from our own overlay windows.
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // idObject / idChild values that mark a whole-window event (vs. a caret,
    // cursor, scrollbar, or child control sub-object).
    public const int OBJID_WINDOW = 0;
    public const int CHILDID_SELF = 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int, int, uint, uint, void> lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // DWMWA_CLOAKED: nonzero when DWM is hiding the window (suspended UWP app,
    // or a window living on a different virtual desktop). Such windows must
    // never be relocated - they're invisible to the user as-is.
    public const int DWMWA_CLOAKED = 14;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int pvAttribute, int cbAttribute);

    // ====================== Session change notifications =====================

    public const uint WM_WTSSESSION_CHANGE = 0x02B1;
    public const int WTS_SESSION_LOCK = 0x7;
    public const int WTS_SESSION_UNLOCK = 0x8;
    public const uint NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    public const uint WTS_CURRENT_SESSION = unchecked((uint)-1);
    // WTS_INFO_CLASS.WTSSessionInfoEx
    public const int WTSSessionInfoEx = 25;
    // WTSINFOEX_LEVEL1_W.SessionFlags values. Win10+ semantics (the
    // documented Win7 lock/unlock inversion predates the 19041 floor);
    // UNKNOWN is 0xFFFFFFFF.
    public const int WTS_SESSIONSTATE_LOCK = 0;
    public const int WTS_SESSIONSTATE_UNLOCK = 1;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, uint sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    // Leading fields of WTSINFOEXW (x64): Level at 0; the Data union starts
    // at 8 because WTSINFOEX_LEVEL1_W carries LARGE_INTEGER members (8-byte
    // alignment). Only the fields before the union's WCHAR arrays are mapped.
    [StructLayout(LayoutKind.Explicit)]
    public struct WTSINFOEX_PREFIX
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int SessionState;
        [FieldOffset(16)] public int SessionFlags;
    }

    // ====================== Process image query =====================

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ====================== Low-level mouse hook =====================

    public const int WH_MOUSE_LL = 14;
    public const int HC_ACTION = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook,
        delegate* unmanaged[Stdcall]<int, IntPtr, IntPtr, IntPtr> lpfn,
        IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    // Cursor-position accessibility event. Touch warps the shared cursor onto a
    // panel monitor via SetCursorPos, which a low-level mouse hook can't see but
    // which fires this event. OBJID_CURSOR marks the cursor object on the event.
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const int OBJID_CURSOR = -9;

    // ====================== GDI regions =====================

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect,
        int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    public static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // Stock objects are owned by the system: never pass one to DeleteObject.
    public const int BLACK_BRUSH = 4;

    [DllImport("gdi32.dll")]
    public static extern IntPtr GetStockObject(int fnObject);
}
