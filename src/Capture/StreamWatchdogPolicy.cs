namespace Nexus.Overlay.Capture;

internal enum WatchdogAction
{
    None,
    Probe,
    Restart,
}

/// <summary>Restarts the WGC session when a forced page repaint (probe) yields no WGC frame; the zero-frame ceiling bounds a probe that never answers.</summary>
internal sealed class StreamWatchdogPolicy
{
    public const int MaxProbeFailures = 3;
    public const int CeilingZeroTicks = 60;
    public const int ProbeEveryZeroTicks = 2;

    private int _zeroTicks;
    private int _probeFailures;
    // Zero-frame ticks seen since the last probe was answered; null when none is outstanding.
    private int? _ticksSinceProbe;

    public int ZeroTicks => _zeroTicks;
    public bool ProbeBroken => _probeFailures >= MaxProbeFailures;

    /// <summary>Feeds one watchdog tick with the frames pumped since the last tick.</summary>
    public WatchdogAction Tick(long framesDelta)
    {
        if (framesDelta > 0)
        {
            _zeroTicks = 0;
            _ticksSinceProbe = null;
            _probeFailures = 0;
            return WatchdogAction.None;
        }
        _zeroTicks++;
        if (_zeroTicks >= CeilingZeroTicks) return Restarted();
        // The answered probe must have a full tick to surface its frame: the answer can land just before a tick.
        if (_ticksSinceProbe is { } seen)
        {
            if (seen >= 1) return Restarted();
            _ticksSinceProbe = seen + 1;
            return WatchdogAction.None;
        }
        if (ProbeBroken) return WatchdogAction.None;
        return _zeroTicks % ProbeEveryZeroTicks == 0 ? WatchdogAction.Probe : WatchdogAction.None;
    }

    /// <summary>The page applied the probe's repaint; a WGC frame must follow.</summary>
    public void OnProbe()
    {
        _probeFailures = 0;
        _ticksSinceProbe = 0;
    }

    public void OnProbeFailed()
    {
        _probeFailures++;
    }

    private WatchdogAction Restarted()
    {
        _zeroTicks = 0;
        _ticksSinceProbe = null;
        _probeFailures = 0;
        return WatchdogAction.Restart;
    }
}
