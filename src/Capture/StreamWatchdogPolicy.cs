namespace Nexus.Overlay.Capture;

internal enum WatchdogAction
{
    None,
    Probe,
    Restart,
    /// <summary>Recreating the WGC session on the same window keeps failing; tear down the whole host (window and WebView2) and build a fresh one.</summary>
    Rebuild,
    /// <summary>The host painted its initial frame and a forced repaint produced no second one: rebuild at once in software compositing, without backoff.</summary>
    RebuildSoftware,
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
    // WGC restarts of any kind in a row that never brought back a frame past the new session's initial one;
    // at the cap the next restart escalates to a full host rebuild.
    public const int MaxStuckRestarts = 3;

    // Zero-frame ticks of a fast-detect window after which the window closes, so a stalled probe cannot keep the fast tick forever.
    public const int FastDetectMaxZeroTicks = 10;

    private readonly bool _fastDetect;
    private long _totalFrames;
    private int _zeroTicks;
    private int _probeFailures;
    private bool _probeInFlight;
    private int _inFlightTicks;
    // Zero-frame ticks seen since the in-flight probe was answered; null while unanswered or idle.
    private int? _ticksSinceAnswer;
    private int _repaintRestarts;
    private int _stuckRestarts;
    // Composition is legitimately suspended (locked workstation, secure desktop): no frames is expected, so never escalate.
    private bool _locked;
    private bool _awaitingInitialFrame;

    /// <param name="fastDetect">True for a GPU-composited host: a host that never got past its initial frame escalates to a software rebuild on its first unanswered repaint.</param>
    public StreamWatchdogPolicy(bool fastDetect = false)
    {
        _fastDetect = fastDetect;
    }

    /// <summary>True while a fast-detect host has delivered exactly its initial frame; the host ticks faster meanwhile.</summary>
    public bool FastDetectWindow => _fastDetect && _totalFrames == 1 && _zeroTicks < FastDetectMaxZeroTicks;

    public int ZeroTicks => _zeroTicks;
    public bool ProbeBroken => _probeFailures >= MaxProbeFailures;
    public bool RepaintRestartsExhausted => _repaintRestarts >= MaxRepaintRestarts;
    /// <summary>A probe was requested and is not yet answered, failed, timed out or dropped; a completion is only accepted while this holds.</summary>
    public bool ProbeInFlight => _probeInFlight;

    /// <summary>Feeds one watchdog tick with the frames pumped since the last tick.</summary>
    public WatchdogAction Tick(long framesDelta, bool sessionLocked = false)
    {
        _locked = sessionLocked;
        var fastWindow = FastDetectWindow;
        if (framesDelta > 0)
        {
            _totalFrames += framesDelta;
            if (_awaitingInitialFrame) _awaitingInitialFrame = false;
            else
            {
                _repaintRestarts = 0;
                _stuckRestarts = 0;
            }
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
                    if (fastWindow)
                    {
                        if (_locked)
                        {
                            ClearProbe();
                            return WatchdogAction.None;
                        }
                        Rebuilt();
                        return WatchdogAction.RebuildSoftware;
                    }
                    if (!RepaintRestartsExhausted) return Restarted(repaint: true);
                    // Capped and the repaint still reached no WGC frame: recreating the session on this window has failed.
                    if (_locked)
                    {
                        ClearProbe();
                        return WatchdogAction.None;
                    }
                    return Rebuilt();
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
        if (!fastWindow && _zeroTicks % ProbeEveryZeroTicks != 0) return WatchdogAction.None;
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
        if (_stuckRestarts >= MaxStuckRestarts)
        {
            if (!_locked) return Rebuilt();
            // Held at the cap: the first dead tick after unlock escalates.
            _zeroTicks = 0;
            ClearProbe();
            _probeFailures = 0;
            return WatchdogAction.None;
        }
        _stuckRestarts++;
        if (repaint) _repaintRestarts++;
        _awaitingInitialFrame = true;
        _zeroTicks = 0;
        ClearProbe();
        _probeFailures = 0;
        return WatchdogAction.Restart;
    }

    private WatchdogAction Rebuilt()
    {
        _repaintRestarts = 0;
        _stuckRestarts = 0;
        _awaitingInitialFrame = false;
        _zeroTicks = 0;
        ClearProbe();
        _probeFailures = 0;
        return WatchdogAction.Rebuild;
    }
}
