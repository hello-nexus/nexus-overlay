using System;

namespace Nexus.Overlay.Capture;

/// <summary>Delay before a stream host that was torn down for a stuck capture is built again, so a machine that never recovers does not thrash.</summary>
internal static class StreamRebuildBackoff
{
    public const long BaseDelayMs = 30_000;
    public const long MaxDelayMs = 300_000;
    // Consecutive watchdog ticks with frames before a rebuilt host counts as recovered; a fresh session's lone initial frame spans one.
    public const int RecoveredFlowingTicks = 2;

    /// <summary>Delay after the given number of rebuilds in a row (1 for the first).</summary>
    public static long DelayMs(int rebuilds)
    {
        if (rebuilds < 1) return 0;
        var shift = Math.Min(rebuilds - 1, 10);
        return Math.Min(MaxDelayMs, BaseDelayMs << shift);
    }
}
