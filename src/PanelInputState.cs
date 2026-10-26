namespace Nexus.Overlay;

/// <summary>
/// Click-through and z-order state of one monitor kiosk, kept apart from the
/// Win32 window so the rules are testable. Every mutator returns whether the
/// window needs its region or z-order re-applied.
/// A click-through window starts with an EMPTY region and only the page's own
/// report widens it, so a dead or unloaded page can never hold input.
/// </summary>
internal sealed class PanelInputState
{
    private HitRegionPlan _plan = HitRegionPlan.Empty;

    public PanelInputState(bool clickThrough, bool topmost)
    {
        ClickThrough = clickThrough;
        Topmost = topmost;
    }

    public bool ClickThrough { get; private set; }
    public bool Topmost { get; private set; }
    public bool Raised { get; private set; }

    /// <summary>Region to apply: the page's report when click-through, else the whole window.</summary>
    public HitRegionPlan Effective => ClickThrough ? _plan : HitRegionPlan.Full;

    /// <summary>A non-topmost kiosk sits at the bottom of the stack unless the page raised it.</summary>
    public bool SinkToBottom => !Topmost && !Raised;

    public bool SetClickThrough(bool value)
    {
        if (value == ClickThrough) return false;
        // Turning it on re-arms the empty region until the page reports again.
        ClickThrough = value;
        _plan = HitRegionPlan.Empty;
        return true;
    }

    public bool ApplyReport(HitRegionPlan plan)
    {
        if (!ClickThrough) return false;
        if (plan.Equals(_plan)) return false;
        _plan = plan;
        return true;
    }

    /// <summary>The page navigated, crashed or hung: fall back to the safe state.</summary>
    public bool PageLost()
    {
        var changed = (ClickThrough && !_plan.Equals(HitRegionPlan.Empty)) || Raised;
        _plan = HitRegionPlan.Empty;
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
