using System;
using System.IO;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Fallback ladder for a stream host whose WGC capture stays dead: the first rebuilt host that still yields
/// no frames switches the stream browser to software compositing (--disable-gpu). The decision is latched for
/// the process and, when a marker path is given, remembered across overlay restarts by that file.
/// One instance per stream host manager.
/// </summary>
internal sealed class StreamRenderMode
{
    /// <summary>Rebuilds in a row (no frames in between) at which software compositing takes over.</summary>
    public const int SoftwareAtRebuild = 1;

    private readonly string? _markerPath;

    public StreamRenderMode(string? markerPath = null)
    {
        _markerPath = markerPath;
        if (markerPath is not null && File.Exists(markerPath))
        {
            Software = true;
            StaleProfileDeletable = false;
        }
    }

    /// <summary>Marker file under the per-user Nexus folder; delete it to give GPU compositing another chance.</summary>
    public static string DefaultMarkerPath() => Path.Combine(
        UserDataPaths.NexusRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
        "stream-software-compositing");

    /// <summary>Software compositing is on for every stream host built from now on.</summary>
    public bool Software { get; private set; }

    /// <summary>False once software compositing was used, because a software browser may still be alive on the -sw profile.</summary>
    public bool StaleProfileDeletable { get; private set; } = true;

    /// <summary>True when this consecutive rebuild count calls for the software mode.</summary>
    public static bool RequiresSoftware(int consecutiveRebuilds) => consecutiveRebuilds >= SoftwareAtRebuild;

    /// <summary>Applies the ladder after a rebuild; true when this call switched the mode.</summary>
    public bool OnRebuilt(int consecutiveRebuilds)
    {
        if (Software || !RequiresSoftware(consecutiveRebuilds)) return false;
        Software = true;
        StaleProfileDeletable = false;
        if (_markerPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
                File.WriteAllText(_markerPath, "");
            }
            catch (Exception ex) { Log.Warn($"stream-hosts could not persist software compositing: {ex.Message}"); }
        }
        return true;
    }
}
