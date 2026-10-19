using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>Geometry for <see cref="TouchCursorGuard"/>, Win32-free for tests. Rects are monitor bounds, Right and Bottom exclusive.</summary>
internal static class CursorGuardGeometry
{
    /// <summary>True when the cursor is on a kept-off panel, or on a kiosk the mouse did not move it onto.</summary>
    public static bool MustLeave(bool onKiosk, bool onKeepOff, bool mouseOnPanel) =>
        onKeepOff || (onKiosk && !mouseOnPanel);

    /// <summary><paramref name="p"/> when it lies on a monitor, otherwise the closest point on the nearest monitor.</summary>
    public static (int X, int Y) ClampToNearest(
        (int X, int Y) p, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors)
    {
        var best = p;
        var bestDist = long.MaxValue;
        foreach (var m in monitors)
        {
            if (!TryClampInto(p, m, out var q)) continue;
            var d = DistSq(p, q);
            if (d < bestDist) { best = q; bestDist = d; }
        }
        return best;
    }

    /// <summary>
    /// True when <paramref name="p"/> is on a monitor, off every kiosk, and
    /// not on the monitor the cursor is on now. The last check covers kiosk
    /// bounds that are stale for the few seconds between a display change and
    /// the kiosk's recreate.
    /// </summary>
    public static bool IsReturnTarget((int X, int Y) p, (int X, int Y) cursor,
        IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors, Func<(int X, int Y), bool> onKiosk)
    {
        var monitor = IndexContaining(p, monitors);
        return monitor >= 0 && monitor != IndexNearest(cursor, monitors) && !onKiosk(p);
    }

    /// <summary>
    /// <paramref name="origin"/> when it is a return target, else the point
    /// nearest <paramref name="cursor"/> on another monitor off every kiosk;
    /// null when no monitor qualifies.
    /// </summary>
    public static (int X, int Y)? ReturnTarget((int X, int Y) origin, (int X, int Y) cursor,
        IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors, Func<(int X, int Y), bool> onKiosk)
    {
        if (IsReturnTarget(origin, cursor, monitors, onKiosk)) return origin;
        var cursorMonitor = IndexNearest(cursor, monitors);
        (int X, int Y)? best = null;
        var bestDist = long.MaxValue;
        for (var i = 0; i < monitors.Count; i++)
        {
            if (i == cursorMonitor || !TryClampInto(cursor, monitors[i], out var q) || onKiosk(q)) continue;
            var d = DistSq(cursor, q);
            if (d < bestDist) { best = q; bestDist = d; }
        }
        return best;
    }

    private static int IndexContaining((int X, int Y) p, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors)
    {
        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            if (p.X >= m.Left && p.X < m.Right && p.Y >= m.Top && p.Y < m.Bottom) return i;
        }
        return -1;
    }

    private static int IndexNearest((int X, int Y) p, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors)
    {
        var best = -1;
        var bestDist = long.MaxValue;
        for (var i = 0; i < monitors.Count; i++)
        {
            if (!TryClampInto(p, monitors[i], out var q)) continue;
            var d = DistSq(p, q);
            if (d < bestDist) { best = i; bestDist = d; }
        }
        return best;
    }

    private static bool TryClampInto((int X, int Y) p, (int Left, int Top, int Right, int Bottom) r, out (int X, int Y) q)
    {
        q = default;
        if (r.Right <= r.Left || r.Bottom <= r.Top) return false;
        q = (Math.Clamp(p.X, r.Left, r.Right - 1), Math.Clamp(p.Y, r.Top, r.Bottom - 1));
        return true;
    }

    private static long DistSq((int X, int Y) a, (int X, int Y) b)
    {
        long dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}
