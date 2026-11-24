namespace Nexus.Overlay.Capture;

/// <summary>
/// Fallback ladder for a stream host whose WGC capture stays dead: the first rebuild keeps GPU compositing,
/// a rebuilt host that still yields no frames switches the stream browser to software compositing
/// (--disable-gpu), and that mode then holds until all hosts are closed. One instance per stream host manager.
/// </summary>
internal sealed class StreamRenderMode
{
    /// <summary>Rebuilds in a row (no frames in between) at which software compositing takes over.</summary>
    public const int SoftwareAtRebuild = 2;

    /// <summary>Software compositing is on for every stream host built from now on, until <see cref="Reset"/>.</summary>
    public bool Software { get; private set; }

    /// <summary>False once this manager has used software compositing, even after <see cref="Reset"/>: a software browser may still be alive on the -sw profile.</summary>
    public bool StaleProfileDeletable { get; private set; } = true;

    /// <summary>True when this consecutive rebuild count calls for the software mode.</summary>
    public static bool RequiresSoftware(int consecutiveRebuilds) => consecutiveRebuilds >= SoftwareAtRebuild;

    /// <summary>Applies the ladder after a rebuild; true when this call switched the mode.</summary>
    public bool OnRebuilt(int consecutiveRebuilds)
    {
        if (Software || !RequiresSoftware(consecutiveRebuilds)) return false;
        Software = true;
        StaleProfileDeletable = false;
        return true;
    }

    /// <summary>All hosts were torn down: the GPU mode gets another chance.</summary>
    public void Reset() => Software = false;
}
