using System;
using Nexus.Overlay.Media;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Windows 10 refuses CreateForWindow (E_INVALIDARG) on tool windows and owned
/// windows but accepts the same off-screen window without WS_EX_TOOLWINDOW
/// (measured on 19045); Windows 11 accepts the tool window. Re-adding the style
/// to a captured window stops its frames within seconds on 19045.
/// </summary>
internal static unsafe class CaptureWindowStyle
{
    // ShObjIdl_core.h: CLSID_TaskbarList, ITaskbarList with
    // HrInit/AddTab/DeleteTab after IUnknown.
    private static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IID_ITaskbarList = new("56FDF342-FD6D-11d0-958A-006097C9A090");
    private const int TaskbarList_HrInit = 3;
    private const int TaskbarList_DeleteTab = 5;
    private const uint CLSCTX_INPROC_SERVER = 1;

    /// <summary>Hidden around the change so the shell re-reads the style (clearing it without the hide/show still gets E_INVALIDARG on 19045); this adds a taskbar button. DwmFlush lets DWM see the style before an item is created.</summary>
    public static void ClearToolWindow(IntPtr hwnd)
    {
        Native.ShowWindow(hwnd, Native.SW_HIDE);
        SetExStyle(hwnd, Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE) & ~Native.WS_EX_TOOLWINDOW);
        Native.ShowWindow(hwnd, Native.SW_SHOWNOACTIVATE);
        Native.DwmFlush();
    }

    public static void DeleteTaskbarTab(IntPtr hwnd)
    {
        var hr = MfInterop.CoCreateInstance(CLSID_TaskbarList, IntPtr.Zero, CLSCTX_INPROC_SERVER, IID_ITaskbarList, out var list);
        if (hr < 0 || list == IntPtr.Zero)
        {
            Log.Warn($"stream-capture: TaskbarList unavailable hr=0x{hr:X8}");
            return;
        }
        try
        {
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Wv2.Slot(list, TaskbarList_HrInit))(list);
            if (hr < 0)
            {
                Log.Warn($"stream-capture: TaskbarList HrInit failed hr=0x{hr:X8}");
                return;
            }
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Wv2.Slot(list, TaskbarList_DeleteTab))(list, hwnd);
            if (hr < 0) Log.Warn($"stream-capture: TaskbarList DeleteTab failed hr=0x{hr:X8}");
        }
        finally { Wv2.Release(list); }
    }

    private static void SetExStyle(IntPtr hwnd, int exStyle)
    {
        Native.SetWindowLongPtrW(hwnd, Native.GWL_EXSTYLE, (nint)(uint)exStyle);
        Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
    }
}
