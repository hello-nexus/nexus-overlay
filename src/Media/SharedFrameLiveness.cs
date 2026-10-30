namespace Nexus.Overlay.Media;

/// <summary>
/// Detects a service reader that stopped consuming while the overlay keeps producing. Frames in
/// shared mode bypass the HTTP send queue, which is what used to fault a stuck service.
/// </summary>
internal sealed class SharedFrameLiveness
{
    public const long StaleAfterMs = 5000;

    private bool _started;
    private long _lastConsumed;
    private long _progressMs;

    /// <summary>True once frames have been published for longer than <see cref="StaleAfterMs"/> with the consumed sequence unchanged.</summary>
    public bool IsStale(long nowMs, long writtenSequence, long consumedSequence)
    {
        if (!_started || consumedSequence != _lastConsumed || consumedSequence >= writtenSequence)
        {
            _started = true;
            _lastConsumed = consumedSequence;
            _progressMs = nowMs;
            return false;
        }
        return nowMs - _progressMs > StaleAfterMs;
    }
}
