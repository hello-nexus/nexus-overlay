using System;

namespace Nexus.Overlay;

/// <summary>Fits a saved dashboard window rect into a monitor work area.</summary>
internal static class DashboardBoundsClamp
{
    // The window rect carries an invisible resize frame on both side edges
    // and the bottom (insetX / insetY). The visible part is what must fit the
    // work area, so the frame may overhang it and the visible size is never
    // shrunk by the frame on each launch.
    internal static (int x, int y, int w, int h) Clamp(
        int x, int y, int w, int h,
        int workLeft, int workTop, int workRight, int workBottom,
        int insetX, int insetY)
    {
        int visW = Math.Min(w - 2 * insetX, workRight - workLeft);
        int visH = Math.Min(h - insetY, workBottom - workTop);
        int visX = Math.Clamp(x + insetX, workLeft, Math.Max(workLeft, workRight - visW));
        int visY = Math.Clamp(y, workTop, Math.Max(workTop, workBottom - visH));
        return (visX - insetX, visY, visW + 2 * insetX, visH + insetY);
    }
}
