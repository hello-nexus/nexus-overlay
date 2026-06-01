using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Win32;

/// <summary>
/// Finds (or forces creation of) the WorkerW window behind the desktop
/// icons.
///
/// 1. Send Progman the magic 0x052C with params (0xD, 0x1) to spawn a
///    sibling WorkerW behind the icons.
/// 2. EnumWindows for top-level WorkerW windows whose first child is NOT
///    SHELLDLL_DefView - the WorkerW with that child hosts the icons; the
///    one without is the empty layer between wallpaper and icons.
///
/// Dormant: the overlay sits at HWND_BOTTOM instead of parenting to WorkerW.
/// </summary>
internal static unsafe class WorkerW
{
    [ThreadStatic] private static IntPtr _foundWorkerW;

    public static IntPtr FindOrSpawn()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        Native.SendMessageTimeoutW(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1),
            Native.SMTO_NORMAL, 1000, out _);

        _foundWorkerW = IntPtr.Zero;
        Native.EnumWindows(&EnumProc, IntPtr.Zero);
        return _foundWorkerW;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnumProc(IntPtr hwnd, IntPtr lParam)
    {
        var defView = Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero) return 1;

        const int bufLen = 64;
        var buf = stackalloc char[bufLen];
        Native.GetClassNameW(hwnd, buf, bufLen);
        var name = new string(buf);
        if (name == "WorkerW")
        {
            _foundWorkerW = hwnd;
            return 0; // FALSE - stop enumerating
        }
        return 1;
    }
}
