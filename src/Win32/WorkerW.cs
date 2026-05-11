using System;
using System.Text;

namespace Qos.Overlay.Win32;

/// <summary>
/// Finds (or forces creation of) the WorkerW window that sits behind the
/// desktop icons. Standard Rainmeter / Wallpaper Engine technique.
///
/// 1. Send Progman the magic 0x052C with params (0xD, 0x1) to force it to
///    spawn a sibling WorkerW behind the icons.
/// 2. EnumWindows for top-level WorkerW windows whose first child is NOT
///    SHELLDLL_DefView - the WorkerW with that child hosts the icons; the
///    one without is the empty layer between wallpaper and icons.
/// </summary>
internal static class WorkerW
{
    public static IntPtr FindOrSpawn()
    {
        var progman = Native.FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        // Returns 0/null when the WorkerW already exists. Either way, after
        // this call the empty WorkerW is enumerable.
        Native.SendMessageTimeout(
            progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1),
            Native.SMTO_NORMAL, 1000, out _);

        IntPtr workerW = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            // The WorkerW we want is one whose first child is NOT
            // SHELLDLL_DefView. There are typically two WorkerW siblings
            // of Progman after the magic message - one hosts the icons,
            // one is empty.
            var defView = Native.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero) return true;

            var sb = new StringBuilder(64);
            Native.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "WorkerW")
            {
                // Empty WorkerW: parent of nothing, sits behind icons.
                workerW = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return workerW;
    }
}
