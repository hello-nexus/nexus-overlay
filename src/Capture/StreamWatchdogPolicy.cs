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
    public const int ProbeTimeoutTicks = 3;
    // Repaint-driven restarts in a row that never brought back a frame past the new session's initial one;
    // at the cap a repaint with no frame no longer restarts.
    public const int MaxRepaintRestarts = 3;

    private int _zeroTicks;
    private int _probeFailures;
    private bool _probeInFlight;
    private int _inFlightTicks;
    // Zero-frame ticks seen since the in-flight probe was answered; null while unanswered or idle.
    private int? _ticksSinceAnswer;
    private int _repaintRestarts;
    private bool _awaitingInitialFrame;

    public int ZeroTicks => _zeroTicks;
    public bool ProbeBroken => _probeFailures >= MaxProbeFailures;
    public bool RepaintRestartsExhausted => _repaintRestarts >= MaxRepaintRestarts;
    /// <summary>A probe was requested and is not yet answered, failed, timed out or dropped; a completion is only accepted while this holds.</summary>
    public bool ProbeInFlight => _probeInFlight;

    /// <summary>Feeds one watchdog tick with the frames pumped since the last tick.</summary>
    public WatchdogAction Tick(long framesDelta)
    {
        if (framesDelta > 0)
        {
            if (_awaitingInitialFrame) _awaitingInitialFrame = false;
            else _repaintRestarts = 0;
            _zeroTicks = 0;
            ClearProbe();
            _probeFailures = 0;
            return WatchdogAction.None;
        }
        _zeroTicks++;
        if (_zeroTicks >= CeilingZeroTicks) return Restarted(repaint: false);
        if (_probeInFlight)
        {
            // The answered probe must have a full tick to surface its frame: the answer can land just before a tick.
            if (_ticksSinceAnswer is { } seen)
            {
                if (seen >= 1)
                {
                    if (!RepaintRestartsExhausted) return Restarted(repaint: true);
                    // Capped: keep probing so a later probe frame can reset the cap; the ceiling still restarts.
                    ClearProbe();
                    return WatchdogAction.None;
                }
                _ticksSinceAnswer = seen + 1;
            }
            else if (++_inFlightTicks >= ProbeTimeoutTicks)
            {
                OnProbeFailed();
            }
            return WatchdogAction.None;
        }
        if (ProbeBroken) return WatchdogAction.None;
        if (_zeroTicks % ProbeEveryZeroTicks != 0) return WatchdogAction.None;
        _probeInFlight = true;
        _inFlightTicks = 0;
        return WatchdogAction.Probe;
    }

    /// <summary>The page applied the probe's repaint; a WGC frame must follow. Ignored unless a probe is in flight and unanswered.</summary>
    public void OnProbe()
    {
        if (!_probeInFlight || _ticksSinceAnswer is not null) return;
        _probeFailures = 0;
        _ticksSinceAnswer = 0;
    }

    /// <summary>The in-flight probe failed or timed out. Ignored unless a probe is in flight and unanswered.</summary>
    public void OnProbeFailed()
    {
        if (!_probeInFlight || _ticksSinceAnswer is not null) return;
        ClearProbe();
        _probeFailures++;
    }

    private void ClearProbe()
    {
        _probeInFlight = false;
        _inFlightTicks = 0;
        _ticksSinceAnswer = null;
    }

    private WatchdogAction Restarted(bool repaint)
    {
        if (repaint) _repaintRestarts++;
        _awaitingInitialFrame = true;
        _zeroTicks = 0;
        ClearProbe();
        _probeFailures = 0;
        return WatchdogAction.Restart;
    }
}
