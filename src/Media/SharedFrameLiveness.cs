namespace Nexus.Overlay.Media;

/// <summary>
/// Detects a service reader that stopped consuming while the overlay keeps producing. Frames in
/// shared mode bypass the HTTP send queue, which is what used to fault a stuck service. The window
/// runs from when the oldest unconsumed write was published, so an idle stretch never counts.
/// </summary>
internal sealed class SharedFrameLiveness
{
    public const long StaleAfterMs = 5000;

    private bool _pending;
    private long _lastConsumed;
    private long _pendingSinceMs;

    /// <summary>Call right after each write. True once the oldest unconsumed write is older than <see cref="StaleAfterMs"/>.</summary>
    public bool IsStale(long nowMs, long writtenSequence, long consumedSequence)
    {
        if (consumedSequence >= writtenSequence)
        {
            _pending = false;
            _lastConsumed = consumedSequence;
            return false;
        }
        if (!_pending || consumedSequence != _lastConsumed)
        {
            _pending = true;
            _lastConsumed = consumedSequence;
            _pendingSinceMs = nowMs;
            return false;
        }
        return nowMs - _pendingSinceMs > StaleAfterMs;
    }
}
