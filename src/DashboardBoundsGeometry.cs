using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>
/// Placement math for the dashboard's saved and default bounds. No Win32
/// dependency, so unit-testable on any platform.
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

    /// <summary>
    /// Indices of the monitors the dashboard may sit on: every non-panel
    /// monitor, or all of them when every monitor hosts a panel.
    /// </summary>
    public static List<int> UsableMonitors(IReadOnlyList<bool> isPanel)
    {
        var usable = new List<int>();
        for (int i = 0; i < isPanel.Count; i++)
            if (!isPanel[i]) usable.Add(i);
        if (usable.Count == 0)
            for (int i = 0; i < isPanel.Count; i++) usable.Add(i);
        return usable;
    }

    /// <summary>
    /// Monitor index for the default placement: the OS primary unless it hosts
    /// a panel, else the first non-panel monitor, else the primary; -1 if none.
    /// </summary>
    public static int DefaultMonitor(IReadOnlyList<(bool Primary, bool Panel)> monitors)
    {
        int primary = -1, firstNonPanel = -1;
        for (int i = 0; i < monitors.Count; i++)
        {
            if (monitors[i].Primary && primary < 0) primary = i;
            if (!monitors[i].Panel && firstNonPanel < 0) firstNonPanel = i;
        }
        if (primary >= 0 && !monitors[primary].Panel) return primary;
        if (firstNonPanel >= 0) return firstNonPanel;
        return primary >= 0 ? primary : (monitors.Count > 0 ? 0 : -1);
    }
}
