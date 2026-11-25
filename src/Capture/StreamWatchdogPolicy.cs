namespace Nexus.Overlay.Capture;

internal enum WatchdogAction
{
    None,
    Probe,
    Restart,
    /// <summary>Recreating the WGC session on the same window keeps failing; tear down the whole host (window and WebView2) and build a fresh one.</summary>
    Rebuild,
    /// <summary>The host painted its initial frame and repeated forced repaints produced no second one: rebuild at once in software compositing, without backoff.</summary>
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

    // Ticks (unlocked) after spawn at which the one-shot fast-detect window closes for good.
    public const int FastDetectMaxTicks = 30;
    // Answered repaints in a row that yielded no WGC frame before a fast-detect host escalates to software.
    public const int FastDetectMisses = 2;
    // Ticks a fast-detect repaint's frame gets after the answer; the grace spans more than one full fast interval after the answer, so a repaint's frame is never judged on a partial tick.
    public const int FastDetectGraceTicks = 2;

    private bool _windowOpen;
    private int _windowTicks;
    private int _fastMisses;
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

    /// <param name="fastDetect">True for a GPU-composited host: a host that never got past its initial frame escalates to a software rebuild after <see cref="FastDetectMisses"/> answered repaints in a row that yield no frame.</param>
    public StreamWatchdogPolicy(bool fastDetect = false)
    {
        _windowOpen = fastDetect;
    }

    /// <summary>True from spawn until a second frame arrives or the window bound passes, never again after; the host ticks faster meanwhile. Closed to probing and the fast tick while the session is locked.</summary>
    public bool FastDetectWindow => _windowOpen && !_locked;

    private void CloseWindow()
    {
        _windowOpen = false;
        _fastMisses = 0;
        _zeroTicks = 0;
        ClearProbe();
        _probeFailures = 0;
    }

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
        if (fastWindow && ++_windowTicks > FastDetectMaxTicks)
        {
            CloseWindow();
            fastWindow = false;
        }
        if (framesDelta > 0)
        {
            _totalFrames += framesDelta;
            if (_totalFrames >= 2) _windowOpen = false;
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
        // Before the initial frame the page may still be loading: the window waits instead of probing.
        if (fastWindow && _totalFrames == 0) return WatchdogAction.None;
        if (_zeroTicks >= CeilingZeroTicks) return Restarted(repaint: false);
        if (_probeInFlight)
        {
            // The answered probe must have a full tick to surface its frame: the answer can land just before a tick.
            if (_ticksSinceAnswer is { } seen)
            {
                if (fastWindow)
                {
                    if (seen < FastDetectGraceTicks)
                    {
                        _ticksSinceAnswer = seen + 1;
                        return WatchdogAction.None;
                    }
                    if (++_fastMisses >= FastDetectMisses)
                    {
                        Rebuilt();
                        return WatchdogAction.RebuildSoftware;
                    }
                    // One repaint without a frame can be a saturated GPU: ask once more before giving up on it.
                    ClearProbe();
                    _probeInFlight = true;
                    return WatchdogAction.Probe;
                }
                if (seen >= 1)
                {
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
        // A busy host inside the fast window times probes out without being broken; the ladder must start fully armed.
        if (!FastDetectWindow) _probeFailures++;
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
        // A restart (for example while locked, before the first frame) ends the one-shot fast window for good.
        _windowOpen = false;
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
        _windowOpen = false;
        _fastMisses = 0;
        _repaintRestarts = 0;
        _stuckRestarts = 0;
        _awaitingInitialFrame = false;
        _zeroTicks = 0;
        ClearProbe();
        _probeFailures = 0;
        return WatchdogAction.Rebuild;
    }
}
