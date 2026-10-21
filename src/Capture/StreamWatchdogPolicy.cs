namespace Nexus.Overlay.Capture;

/// <summary>
/// Restart decision for the stream capture watchdog. WGC is change-driven, so
/// zero frames means either a dead capture or a page with nothing to draw.
/// The signal that tells them apart is what a restart yields: a recreated
/// session always delivers one initial frame, so a restart followed by no
/// more than that frame is unproductive (static page, or still dead) and the
/// zero-frame ticks required before the next restart double up to a cap. A
/// restart followed by further frames proves the capture was stale and
/// resets the requirement.
/// </summary>
internal sealed class StreamWatchdogPolicy
{
    public const int BaseZeroTicks = 2;
    public const int MaxZeroTicks = 60;

    private int _zeroTicks;
    private int _requiredZeroTicks = BaseZeroTicks;
    private bool _judgingRestart;
    private long _framesSinceRestart;

    public int RequiredZeroTicks => _requiredZeroTicks;

    /// <summary>Feeds one watchdog tick with the frames pumped since the last
    /// tick. Returns true when the capture session should be recreated.</summary>
    public bool Tick(long framesDelta)
    {
        if (framesDelta > 0)
        {
            _zeroTicks = 0;
            if (_judgingRestart)
            {
                _framesSinceRestart += framesDelta;
                if (_framesSinceRestart > 1)
                {
                    _judgingRestart = false;
                    _requiredZeroTicks = BaseZeroTicks;
                }
            }
            return false;
        }
        if (++_zeroTicks < _requiredZeroTicks) return false;
        _zeroTicks = 0;
        if (_judgingRestart)
            _requiredZeroTicks = System.Math.Min(_requiredZeroTicks * 2, MaxZeroTicks);
        _judgingRestart = true;
        _framesSinceRestart = 0;
        return true;
    }
}
