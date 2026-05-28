using System;

namespace Nexus.Overlay;

/// <summary>
/// Pure geometry for <see cref="PanelMonitorGuard"/>: where to drop a window
/// that has been evicted off the panel monitor. Kept free of any Win32
/// dependency so it can be unit-tested on any platform.
/// </summary>
internal static class PanelGuardGeometry
{
    /// <summary>
    /// Returns the top-left at which to place a window of size
    /// <paramref name="winW"/> × <paramref name="winH"/> so it sits fully
    /// inside the work area. The window is shifted the minimum amount needed —
    /// clamped to the nearest work-area edge — which naturally keeps it on the
    /// side it came from. When the window is larger than the work area in a
    /// dimension, it pins to that edge's origin.
    /// </summary>
    public static (int Left, int Top) ClampTopLeft(
        int winLeft, int winTop, int winW, int winH,
        int workLeft, int workTop, int workRight, int workBottom)
    {
        int workW = workRight - workLeft;
        int workH = workBottom - workTop;
        int left = winW <= workW ? Math.Clamp(winLeft, workLeft, workRight - winW) : workLeft;
        int top = winH <= workH ? Math.Clamp(winTop, workTop, workBottom - winH) : workTop;
        return (left, top);
    }
}
