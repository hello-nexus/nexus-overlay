using System;

namespace Nexus.Overlay;

/// <summary>Pure size and animation curve of the display identify badge.</summary>
internal static class IdentifyBadge
{
    public const int FadeInMs = 180;
    public const int FadeOutMs = 350;
    private const double StartScale = 0.88;
    private const double SideFraction = 0.30;

    /// <summary>Square badge edge for a monitor, in pixels.</summary>
    public static int Side(int monitorWidth, int monitorHeight)
        => Math.Max(1, (int)Math.Round(Math.Min(monitorWidth, monitorHeight) * SideFraction));

    /// <summary>
    /// Scale (1 = full size) and window alpha (0-255) at <paramref name="elapsedMs"/> of a
    /// <paramref name="totalMs"/> show. The fade out is anchored to the end so a late start still ends on time.
    /// </summary>
    public static (double Scale, byte Alpha) Frame(long elapsedMs, long totalMs)
    {
        var inT = Math.Clamp(elapsedMs / (double)FadeInMs, 0, 1);
        var eased = 1 - (1 - inT) * (1 - inT);
        var outT = Math.Clamp((totalMs - elapsedMs) / (double)FadeOutMs, 0, 1);
        var alpha = Math.Min(inT, outT);
        return (StartScale + (1 - StartScale) * eased, (byte)Math.Round(alpha * 255));
    }

    /// <summary>A service token different from the last seen one is a new identify, since the service counter restarts at zero.</summary>
    public static bool IsNewToken(long? lastSeen, long token) => lastSeen != token;

    /// <summary>Whether the time left covers a full fade in and fade out; anything shorter is skipped.</summary>
    public static bool CanShow(long remainingMs) => remainingMs >= FadeInMs + FadeOutMs;

    /// <summary>True while the badge is fading in or out; between the fades it is static.</summary>
    public static bool IsAnimating(long elapsedMs, long totalMs)
        => elapsedMs < FadeInMs || totalMs - elapsedMs < FadeOutMs;

    public static bool IsDone(long elapsedMs, long totalMs) => elapsedMs >= totalMs;
}
