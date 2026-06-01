using System;

namespace Nexus.Overlay;

/// <summary>
/// Geometry for <see cref="PanelMonitorGuard"/>: where to drop a window
/// evicted off the panel monitor. No Win32 dependency, so unit-testable on
/// any platform.
/// </summary>
internal static class PanelGuardGeometry
{
    /// <summary>
    /// Returns the top-left at which to place a window of size
    /// <paramref name="winW"/> × <paramref name="winH"/> so it sits fully
    /// inside the work area. Clamps to the nearest work-area edge, keeping it
    /// on the side it came from. A window larger than the work area in a
    /// dimension pins to that edge's origin.
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
