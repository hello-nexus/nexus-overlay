using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>Geometry for <see cref="TouchCursorGuard"/>, Win32-free for tests. Rects are monitor bounds, Right and Bottom exclusive.</summary>
internal static class CursorGuardGeometry
{
    /// <summary>Signature Windows puts in the extra info of mouse input promoted from touch or pen.</summary>
    public static bool IsTouchOrPen(nuint extraInfo) => (extraInfo & 0xFFFFFF00u) == 0xFF515700u;

    /// <summary>True when the cursor is on a kept-off panel, or on a kiosk the mouse did not move it onto.</summary>
    public static bool MustLeave(bool onKiosk, bool onKeepOff, bool mouseOnPanel) =>
        onKeepOff || (onKiosk && !mouseOnPanel);

    /// <summary>
    /// <paramref name="p"/> when it lies on a monitor, otherwise its closest
    /// point on the monitor holding <paramref name="cursor"/>, where Windows
    /// keeps the cursor when a move runs off the desktop.
    /// </summary>
    public static (int X, int Y) ClampToCursorMonitor((int X, int Y) p, (int X, int Y) cursor,
        IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors)
    {
        if (IndexContaining(p, monitors) >= 0) return p;
        var monitor = IndexNearest(cursor, monitors);
        return monitor >= 0 && TryClampInto(p, monitors[monitor], out var q) ? q : p;
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

    public static int IndexContaining((int X, int Y) p, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors)
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

    public static bool OnAnyMonitor((int X, int Y) p, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> monitors) =>
        IndexContaining(p, monitors) >= 0;

    public static (int X, int Y) ClampInto((int X, int Y) p, (int Left, int Top, int Right, int Bottom) r) =>
        (Math.Clamp(p.X, r.Left, Math.Max(r.Left, r.Right - 1)), Math.Clamp(p.Y, r.Top, Math.Max(r.Top, r.Bottom - 1)));

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

internal enum MouseEntryVerdict { None, Accepted, Rejected }

/// <summary>
/// Tells a mouse that Windows slid onto a kiosk from a touch teleport. A mouse
/// pushing past an edge, through the dead zone beside a kiosk, or across the
/// gap between two kiosks has a hook point off every monitor; Windows then
/// slides the cursor to the nearest point of the monitor beyond. The push arms
/// the tracker. A cursor move onto a kiosk soon after, landing near the
/// push point's closest spot on that kiosk and not against the push direction,
/// is the mouse's own. A touch teleport lands at the tap, with no push before it.
/// Resting jitter never pushes, so it never arms. The armed state lives until
/// it is judged or expires, so the hook and the cursor-move callback may run in
/// either order.
/// </summary>
internal struct MouseEntryTracker
{
    public const long WindowMs = 100;
    // A fixed physical-pixel bound around the clamped push point. Windows lands
    // the cursor on that point, so only rounding and the DPI mapping between
    // monitors separate the two.
    public const int SlideTolerancePx = 96;
    private long _atMs;
    private bool _armed;
    private (int X, int Y) _raw;
    private (int X, int Y) _from;

    public readonly bool Armed => _armed;

    public void Arm((int X, int Y) rawPush, (int X, int Y) cursorBefore, long nowMs)
    {
        _armed = true;
        _raw = rawPush;
        _from = cursorBefore;
        _atMs = nowMs;
    }

    /// <summary>
    /// The mouse hook's side of the judgement: an event whose point is on a kiosk
    /// while the cursor already is means the slide's cursor move has happened,
    /// whether or not its callback has run yet.
    /// </summary>
    public static MouseEntryVerdict JudgeAtHook(ref MouseEntryTracker tracker, bool pointOnKiosk, bool cursorOnKiosk,
        (int X, int Y) cursor, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> kiosks, long nowMs, out int slidePx)
    {
        slidePx = 0;
        return pointOnKiosk && cursorOnKiosk ? tracker.Judge(cursor, kiosks, nowMs, out slidePx) : MouseEntryVerdict.None;
    }

    /// <summary>
    /// Judges a cursor move that left the cursor at <paramref name="cursorNow"/>.
    /// None when nothing armed it, it expired, or the cursor is on no kiosk;
    /// Accepted consumes the push; Rejected consumes it too, including a push
    /// that began on the kiosk the cursor is on.
    /// </summary>
    public MouseEntryVerdict Judge((int X, int Y) cursorNow,
        IReadOnlyList<(int Left, int Top, int Right, int Bottom)> kiosks, long nowMs, out int slidePx)
    {
        slidePx = 0;
        if (!_armed) return MouseEntryVerdict.None;
        if (nowMs - _atMs > WindowMs) { _armed = false; return MouseEntryVerdict.None; }
        var kiosk = CursorGuardGeometry.IndexContaining(cursorNow, kiosks);
        if (kiosk < 0) return MouseEntryVerdict.None;
        _armed = false;
        // A slide comes from outside the kiosk it lands on; a push that began on this kiosk
        // moves nothing onto it, so a cursor already there was put there some other way.
        if (CursorGuardGeometry.IndexContaining(_from, new[] { kiosks[kiosk] }) >= 0) return MouseEntryVerdict.Rejected;
        var near = CursorGuardGeometry.ClampInto(_raw, kiosks[kiosk]);
        long sx = cursorNow.X - near.X, sy = cursorNow.Y - near.Y;
        slidePx = (int)Math.Sqrt(sx * sx + sy * sy);
        long px = _raw.X - _from.X, py = _raw.Y - _from.Y;
        long lx = cursorNow.X - _from.X, ly = cursorNow.Y - _from.Y;
        var withPush = px * lx + py * ly >= 0;
        return withPush && slidePx <= SlideTolerancePx ? MouseEntryVerdict.Accepted : MouseEntryVerdict.Rejected;
    }
}
