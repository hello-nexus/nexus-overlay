using System;

namespace Nexus.Overlay;

/// <summary>
/// Where the dashboard goes when it would open on a monitor a panel kiosk
/// covers. No Win32 dependency, so unit-testable on any platform.
/// </summary>
internal static class DashboardPlacement
{
    /// <summary>
    /// Index of the monitor to move onto: the primary when it is not a panel
    /// monitor, else the first one that is not. -1 when every monitor is a panel.
    /// </summary>
    public static int PickTarget(bool[] panel, bool[] primary)
    {
        for (int i = 0; i < panel.Length; i++)
            if (primary[i] && !panel[i]) return i;
        for (int i = 0; i < panel.Length; i++)
            if (!panel[i]) return i;
        return -1;
    }

    /// <summary>
    /// Window rect of <paramref name="winW"/> x <paramref name="winH"/>
    /// (already scaled for the target monitor) centered on the work area. The
    /// visible part is clamped to the work area; the invisible resize frame
    /// (<paramref name="frameX"/>, <paramref name="frameY"/>) sits outside it.
    /// </summary>
    public static (int X, int Y, int W, int H) Center(
        int winW, int winH, int frameX, int frameY,
        int workLeft, int workTop, int workRight, int workBottom)
    {
        int workW = workRight - workLeft;
        int workH = workBottom - workTop;
        int w = Math.Min(winW - 2 * frameX, workW);
        int h = Math.Min(winH - frameY, workH);
        return (workLeft + (workW - w) / 2 - frameX, workTop + (workH - h) / 2, w + 2 * frameX, h + frameY);
    }
}
