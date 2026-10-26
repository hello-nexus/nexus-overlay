namespace Nexus.Overlay;

/// <summary>
/// Window-region and z-order state of one monitor kiosk, kept apart from the
/// Win32 window so the rules are testable. Mutators return whether the window
/// needs its region or z-order re-applied.
/// A see-through kiosk (click-through on or off) starts with an EMPTY region
/// and only the page's own report widens it, so a dead or unloaded page can
/// never hold input. An opaque kiosk keeps the whole window.
/// A transient full-window report carries a lease the page must renew; a
/// lapsed lease reverts to the last non-lease region and lowers any raise.
/// </summary>
internal sealed class PanelInputState
{
    /// <summary>The page renews a lease every 2 s; three missed renewals lapse it.</summary>
    internal const long LeaseMs = 6_000;

    private HitRegionPlan _base = HitRegionPlan.Empty;
    private long _leaseExpiresAt;

    public PanelInputState(bool seeThrough, bool topmost)
    {
        SeeThrough = seeThrough;
        Topmost = topmost;
    }

    public bool SeeThrough { get; }
    public bool Topmost { get; private set; }
    public bool Raised { get; private set; }
    public bool LeaseActive { get; private set; }

    /// <summary>Region to apply.</summary>
    public HitRegionPlan Effective => !SeeThrough || LeaseActive ? HitRegionPlan.Full : _base;

    /// <summary>A non-topmost kiosk sits at the bottom of the stack unless the page raised it.</summary>
    public bool SinkToBottom => !Topmost && !Raised;

    /// <param name="nowMs">Monotonic milliseconds, for lease expiry.</param>
    public bool ApplyReport(HitRegionPlan plan, long nowMs)
    {
        if (!SeeThrough) return false;
        var before = Effective;
        if (plan.Lease)
        {
            LeaseActive = true;
            _leaseExpiresAt = nowMs + LeaseMs;
        }
        else
        {
            LeaseActive = false;
            _base = plan;
        }
        return !Effective.Equals(before);
    }

    /// <summary>
    /// Lapse a lease that was not renewed in time. Returns true when the region
    /// or the raise changed. Raise is lowered even when the lease alone did not
    /// change the region, since the transient state that raised it is gone.
    /// </summary>
    public bool Expire(long nowMs, out bool raiseLowered)
    {
        raiseLowered = false;
        if (!LeaseActive || nowMs < _leaseExpiresAt) return false;
        LeaseActive = false;
        raiseLowered = Raised;
        Raised = false;
        return true;
    }

    /// <summary>The page navigated, crashed, hung or was told to re-report: safe state.</summary>
    public bool PageLost()
    {
        var changed = LeaseActive || Raised || (SeeThrough && !_base.Equals(HitRegionPlan.Empty));
        _base = HitRegionPlan.Empty;
        LeaseActive = false;
        Raised = false;
        return changed;
    }

    public bool SetRaised(bool value)
    {
        if (Topmost) value = false;
        if (value == Raised) return false;
        Raised = value;
        return true;
    }

    public bool SetTopmost(bool value)
    {
        if (value == Topmost) return false;
        Topmost = value;
        Raised = false;
        return true;
    }
}
