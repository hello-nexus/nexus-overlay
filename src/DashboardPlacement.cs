using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>One display for <see cref="DashboardPlacement"/>: screen-coordinate bounds.</summary>
internal readonly record struct PlacementMonitor(string Device, bool Primary, int Left, int Top, int Right, int Bottom);

/// <summary>
/// Where the dashboard goes when it would show on a monitor a panel kiosk
/// covers. No Win32 dependency, so unit-testable on any platform.
/// </summary>
internal static class DashboardPlacement
{
    /// <summary>
    /// Index of the monitor to move the window onto, or -1 to leave it. The
    /// window's monitor is the one it overlaps most (else the nearest); it only
    /// moves when that monitor is a panel. The target is the primary monitor
    /// when it is not a panel, else the first monitor that is not.
    /// </summary>
    public static int PickTarget(
        IReadOnlyList<PlacementMonitor> monitors, IReadOnlyCollection<string> panelDevices,
        int left, int top, int right, int bottom)
    {
        var current = MonitorOf(monitors, left, top, right, bottom);
        if (current < 0 || !panelDevices.Contains(monitors[current].Device)) return -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Primary && !panelDevices.Contains(monitors[i].Device)) return i;
        }
        for (int i = 0; i < monitors.Count; i++)
        {
            if (!panelDevices.Contains(monitors[i].Device)) return i;
        }
        return -1;
    }

    private static int MonitorOf(IReadOnlyList<PlacementMonitor> monitors, int left, int top, int right, int bottom)
    {
        int best = -1;
        long bestOverlap = 0;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            long ow = Math.Min(right, m.Right) - Math.Max(left, m.Left);
            long oh = Math.Min(bottom, m.Bottom) - Math.Max(top, m.Top);
            var overlap = ow > 0 && oh > 0 ? ow * oh : 0;
            if (overlap > bestOverlap) { best = i; bestOverlap = overlap; }
        }
        if (best >= 0) return best;

        long cx = ((long)left + right) / 2, cy = ((long)top + bottom) / 2;
        long bestDist = long.MaxValue;
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            long dx = Math.Max(Math.Max(m.Left - cx, 0), cx - m.Right);
            long dy = Math.Max(Math.Max(m.Top - cy, 0), cy - m.Bottom);
            var d = dx * dx + dy * dy;
            if (d < bestDist) { best = i; bestDist = d; }
        }
        return best;
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
