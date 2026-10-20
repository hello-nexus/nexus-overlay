using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>What the guard needs to know about the desktop; Win32-free so the machine runs in tests.</summary>
internal interface ICursorGuardWorld
{
    IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Monitors { get; }

    IReadOnlyList<(int Left, int Top, int Right, int Bottom)> Kiosks { get; }

    bool HasKeepOff { get; }

    bool OnKeepOffPanel((int X, int Y) p);

    /// <summary>One cheap query: the monitor at the point right now is the cached one with this rect.</summary>
    bool MonitorStillAt((int X, int Y) p, (int Left, int Top, int Right, int Bottom) cachedRect);

    /// <summary>Re-reads the whole monitor layout; costly, so called only when the cheap query misses.</summary>
    void RefreshMonitors();
}

/// <summary>Outcome of one mouse hook event: swallow it, and the slide length when it let an entry through.</summary>
internal readonly record struct GuardHookResult(bool Block, int AcceptedSlidePx)
{
    public const int NoEntry = -1;

    public bool EntryAccepted => AcceptedSlidePx >= 0;
}

internal enum GuardEventKind { None, EntryAccepted, MoveCursor, HoldStranded }

/// <summary>Outcome of one cursor-move event: what the Win32 side must do and log.</summary>
internal readonly record struct GuardEventResult(
    GuardEventKind Kind, (int X, int Y) Target = default, string Reason = "", int SlidePx = 0);

/// <summary>
/// The decisions of <see cref="TouchCursorGuard"/>, with every Win32 call replaced by an
/// input or an output so event sequences can be replayed in tests. The hook and the
/// cursor-move callback feed it and carry out what it returns.
/// </summary>
internal sealed class CursorGuardMachine
{
    private readonly ICursorGuardWorld _world;
    private (int X, int Y) _lastMousePt;
    // Whether _lastMousePt was on a kiosk when recorded. Kept as a flag because
    // a display change moves the kiosk bounds the point would be tested against.
    private bool _mouseOnKiosk;
    private (int X, int Y)? _strandedFrom;
    private MouseEntryTracker _mouseEntry;
    // The tracker is already consumed when the cursor-move callback snaps the
    // cursor back, so a rejection the hook made is kept for its log reason.
    private long _hookRejectedAtMs = long.MinValue / 2;

    public CursorGuardMachine(ICursorGuardWorld world) => _world = world;

    public (int X, int Y) LastMousePt => _lastMousePt;

    public bool MouseOnKiosk => _mouseOnKiosk;

    public (int X, int Y)? StrandedFrom => _strandedFrom;

    public bool EntryArmed => _mouseEntry.Armed;

    /// <summary>The first kiosk opened: the guard starts from where the cursor is.</summary>
    public void Start((int X, int Y) cursor)
    {
        _mouseEntry = default;
        _hookRejectedAtMs = long.MinValue / 2;
        _lastMousePt = cursor;
        _mouseOnKiosk = InKiosk(cursor);
        _strandedFrom = null;
    }

    /// <summary>
    /// A kiosk opened under a held guard: a mouse still resting at its recorded point may
    /// now be on it. A cursor anywhere else was moved by something other than the mouse,
    /// so the flag stays.
    /// </summary>
    public void KioskOpened((int X, int Y) cursor)
    {
        if (!_mouseOnKiosk && cursor == _lastMousePt) _mouseOnKiosk = InKiosk(cursor);
    }

    /// <param name="isWmMouseMove">The event is a WM_MOUSEMOVE, whoever produced it.</param>
    /// <param name="isUntaggedMouseMove">A WM_MOUSEMOVE without the touch or pen signature.</param>
    public GuardHookResult Hook((int X, int Y) raw, (int X, int Y) cursor, bool isWmMouseMove, bool isUntaggedMouseMove,
        long nowMs)
    {
        var monitors = _world.Monitors;
        var pt = CursorGuardGeometry.ClampToCursorMonitor(raw, cursor, monitors);
        var ptOnKiosk = InKiosk(pt);
        var entry = MouseEntryVerdict.None;
        var slidePx = 0;
        var passThrough = false;
        var wall = (ptOnKiosk || _world.HasKeepOff) && isWmMouseMove
            && _world.OnKeepOffPanel(pt) && !_world.OnKeepOffPanel(cursor);

        // A push still waiting for its slide is judged before this event can arm another
        // one over it: the cursor being on a kiosk now is that slide having happened.
        if (!wall && isUntaggedMouseMove && ptOnKiosk && _mouseEntry.Armed && InKiosk(cursor) && !_world.OnKeepOffPanel(cursor))
            entry = MouseEntryTracker.JudgeAtHook(ref _mouseEntry, ptOnKiosk, true, cursor, _world.Kiosks, nowMs, out slidePx);

        if (isUntaggedMouseMove && !CursorGuardGeometry.OnAnyMonitor(raw, monitors))
            _mouseEntry.Arm(raw, cursor, nowMs);

        if (wall) return new GuardHookResult(true, GuardHookResult.NoEntry); // the panel is a wall; the cursor stays where it is

        if (ptOnKiosk || _world.HasKeepOff)
        {
            if (entry == MouseEntryVerdict.Accepted)
            {
                AcceptEntry(cursor);
            }
            else if (ptOnKiosk && InKiosk(cursor) && !_mouseOnKiosk && _strandedFrom is null
                && RefreshedIsReturnTarget(_lastMousePt, cursor))
            {
                passThrough = true;
                if (entry == MouseEntryVerdict.Rejected) _hookRejectedAtMs = nowMs;
            }
        }

        if (passThrough) return new GuardHookResult(false, GuardHookResult.NoEntry);
        _lastMousePt = pt;
        _mouseOnKiosk = ptOnKiosk;
        if (!ptOnKiosk) _strandedFrom = null;
        return new GuardHookResult(false, entry == MouseEntryVerdict.Accepted ? slidePx : GuardHookResult.NoEntry);
    }

    public GuardEventResult CursorMoved((int X, int Y) cursor, long nowMs)
    {
        var keepOff = _world.OnKeepOffPanel(cursor);
        var entry = MouseEntryVerdict.None;
        var slidePx = 0;
        if (!keepOff && _mouseEntry.Armed && InKiosk(cursor))
            entry = _mouseEntry.Judge(cursor, _world.Kiosks, nowMs, out slidePx);
        if (entry == MouseEntryVerdict.Accepted)
        {
            AcceptEntry(cursor);
            return new GuardEventResult(GuardEventKind.EntryAccepted, SlidePx: slidePx);
        }

        var mouseOnPanel = _mouseOnKiosk && _strandedFrom is null;
        if (!CursorGuardGeometry.MustLeave(InKiosk(cursor), keepOff, mouseOnPanel)) return default;

        var origin = _strandedFrom ?? _lastMousePt;
        var found = CursorGuardGeometry.ReturnTarget(origin, cursor, _world.Monitors, InKiosk);
        // A display change can land before the guard hears of it; the layout is re-read only
        // when the cached answer points at a monitor that is gone, or at none.
        if (_strandedFrom is null && (found is not { } live || !TargetStillThere(live)))
        {
            _world.RefreshMonitors();
            found = CursorGuardGeometry.ReturnTarget(origin, cursor, _world.Monitors, InKiosk);
        }
        if (found is { } target)
        {
            var reason = _strandedFrom is not null ? "display change" : keepOff ? "keep-off"
                : entry == MouseEntryVerdict.Rejected || nowMs - _hookRejectedAtMs <= MouseEntryTracker.WindowMs
                    ? "gap entry rejected, treated as touch" : "touch";
            if (target != origin) reason += ", nearest monitor";
            _strandedFrom = null;
            _lastMousePt = target;
            _mouseOnKiosk = false;
            return new GuardEventResult(GuardEventKind.MoveCursor, target, reason);
        }
        if (!mouseOnPanel && _strandedFrom is null)
        {
            _strandedFrom = origin;
            return new GuardEventResult(GuardEventKind.HoldStranded, origin);
        }
        return default;
    }

    private void AcceptEntry((int X, int Y) cursor)
    {
        _strandedFrom = null;
        _lastMousePt = cursor;
        _mouseOnKiosk = true;
    }

    private bool InKiosk((int X, int Y) p)
    {
        var kiosks = _world.Kiosks;
        for (var i = 0; i < kiosks.Count; i++)
        {
            var k = kiosks[i];
            if (p.X >= k.Left && p.X < k.Right && p.Y >= k.Top && p.Y < k.Bottom) return true;
        }
        return false;
    }

    private bool TargetStillThere((int X, int Y) target)
    {
        var monitors = _world.Monitors;
        var i = CursorGuardGeometry.IndexContaining(target, monitors);
        return i >= 0 && _world.MonitorStillAt(target, monitors[i]);
    }

    private bool RefreshedIsReturnTarget((int X, int Y) p, (int X, int Y) cursor)
    {
        if (CursorGuardGeometry.IsReturnTarget(p, cursor, _world.Monitors, InKiosk) && TargetStillThere(p)) return true;
        _world.RefreshMonitors();
        return CursorGuardGeometry.IsReturnTarget(p, cursor, _world.Monitors, InKiosk);
    }
}
