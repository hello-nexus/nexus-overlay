using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>
/// Reachability check for the dashboard's saved bounds. No Win32 dependency,
/// so unit-testable on any platform.
/// </summary>
internal static class DashboardBoundsGeometry
{
    /// <summary>
    /// True when the title-bar strip <c>[left, right) x [top, top + stripHeight)</c>
    /// overlaps a single work area by at least <paramref name="minWidth"/> x
    /// <paramref name="minHeight"/> px, i.e. the user can grab it to drag.
    /// </summary>
    public static bool StripReachable(
        int left, int top, int right, int stripHeight,
        IEnumerable<(int Left, int Top, int Right, int Bottom)> workAreas,
        int minWidth, int minHeight)
    {
        int bottom = top + stripHeight;
        foreach (var w in workAreas)
        {
            int ix = Math.Min(right, w.Right) - Math.Max(left, w.Left);
            int iy = Math.Min(bottom, w.Bottom) - Math.Max(top, w.Top);
            if (ix >= minWidth && iy >= minHeight) return true;
        }
        return false;
    }
}
