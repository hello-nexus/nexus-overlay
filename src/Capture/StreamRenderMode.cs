namespace Nexus.Overlay.Capture;

/// <summary>
/// Fallback ladder for a stream host whose WGC capture stays dead: the first rebuild keeps GPU compositing,
/// a rebuilt host that still yields no frames switches the stream browser to software compositing
/// (--disable-gpu), and that mode then holds for the process lifetime.
/// </summary>
internal static class StreamRenderMode
{
    /// <summary>Rebuilds in a row (no frames in between) at which software compositing takes over.</summary>
    public const int SoftwareAtRebuild = 2;

    /// <summary>Software compositing is on for every stream host built from now on.</summary>
    public static bool Software { get; private set; }

    /// <summary>True when this consecutive rebuild count calls for the software mode.</summary>
    public static bool RequiresSoftware(int consecutiveRebuilds) => consecutiveRebuilds >= SoftwareAtRebuild;

    /// <summary>Applies the ladder after a rebuild; true when this call switched the mode.</summary>
    public static bool OnRebuilt(int consecutiveRebuilds)
    {
        if (Software || !RequiresSoftware(consecutiveRebuilds)) return false;
        Software = true;
        return true;
    }
}
