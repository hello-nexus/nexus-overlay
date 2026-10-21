namespace Nexus.Overlay.Capture;

internal enum WatchdogAction
{
    None,
    Probe,
    Restart,
}

/// <summary>Restarts the WGC session only when the WebView rendered something new (probe hash changed) while WGC delivered no frames; the zero-frame ceiling bounds a probe that never changes or never answers.</summary>
internal sealed class StreamWatchdogPolicy
{
    public const int MaxProbeFailures = 3;
    public const int CeilingZeroTicks = 60;

    private int _zeroTicks;
    private int _probeFailures;
    private ulong? _lastHash;

    public int ZeroTicks => _zeroTicks;
    public bool ProbeBroken => _probeFailures >= MaxProbeFailures;

    /// <summary>Feeds one watchdog tick with the frames pumped since the last tick.</summary>
    public WatchdogAction Tick(long framesDelta)
    {
        if (framesDelta > 0)
        {
            _zeroTicks = 0;
            _lastHash = null;
            return WatchdogAction.None;
        }
        _zeroTicks++;
        if (_zeroTicks >= CeilingZeroTicks) return Restarted();
        if (ProbeBroken) return WatchdogAction.None;
        return WatchdogAction.Probe;
    }

    /// <summary>A probe produced an image hash. True when the capture is stale and must be recreated.</summary>
    public bool OnProbe(ulong hash)
    {
        _probeFailures = 0;
        // The first probe of a run is a baseline; the page may have changed before the run began.
        var changed = _lastHash is { } previous && previous != hash && _zeroTicks >= 2;
        _lastHash = hash;
        if (!changed) return false;
        Restarted();
        return true;
    }

    public void OnProbeFailed()
    {
        _probeFailures++;
    }

    private WatchdogAction Restarted()
    {
        _zeroTicks = 0;
        _lastHash = null;
        return WatchdogAction.Restart;
    }
}
