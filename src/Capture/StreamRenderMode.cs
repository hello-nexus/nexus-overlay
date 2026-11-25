using System;
using System.IO;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Software compositing decision for the stream browser: it starts in software compositing (--disable-gpu) when the
/// installed WebView2 runtime major is at or past <see cref="SoftwareFromRuntimeMajor"/>, and otherwise switches
/// when a stream host's WGC recreates yield no frames (the host asks for a rebuild) or a host never gets past its
/// initial frame. The decision is latched for the process and, when a marker path is given, remembered across
/// overlay restarts by that file; the runtime gate itself never writes the marker.
/// The marker holds a stamp "build|runtime"; a marker whose stamp differs is deleted so GPU compositing is tried
/// again once. An empty runtime part on either side is unknown and only the build part is compared.
/// One instance per stream host manager.
/// </summary>
internal sealed class StreamRenderMode
{
    /// <summary>Rebuilds in a row (no frames in between) at which software compositing takes over.</summary>
    public const int SoftwareAtRebuild = 1;

    /// <summary>First WebView2 runtime major with the regression: the off-screen stream host gets no WGC frames after the first, so stream hosts start in software compositing.</summary>
    public const int SoftwareFromRuntimeMajor = 158;

    private readonly string? _markerPath;

    private readonly string _stamp;

    public StreamRenderMode(string? markerPath = null, string stamp = "", int? runtimeMajor = null)
    {
        _markerPath = string.IsNullOrEmpty(markerPath) ? null : markerPath;
        _stamp = stamp;
        if (RuntimeRequiresSoftware(runtimeMajor))
        {
            Software = true;
            StaleProfileDeletable = false;
        }
        if (_markerPath is null) return;
        try
        {
            if (!File.Exists(_markerPath)) return;
            if (StampMatches(File.ReadAllText(_markerPath).Trim(), _stamp))
            {
                Software = true;
                StaleProfileDeletable = false;
            }
            else File.Delete(_markerPath);
        }
        catch (Exception ex) { Log.Warn($"stream-hosts could not read software compositing marker: {ex.Message}"); }
    }

    private static bool StampMatches(string written, string current)
    {
        if (written == current) return true;
        var w = written.Split('|');
        var c = current.Split('|');
        if (w.Length != 2 || c.Length != 2) return false;
        return w[0] == c[0] && (w[1].Length == 0 || c[1].Length == 0);
    }

    /// <summary>Marker file under the per-user Nexus folder; delete it to give GPU compositing another chance.</summary>
    public static string? DefaultMarkerPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(UserDataPaths.NexusRoot(local), "stream-software-compositing");
    }

    /// <summary>Software compositing is on for every stream host built from now on.</summary>
    public bool Software { get; private set; }

    /// <summary>False once software compositing was used, because a software browser may still be alive on the -sw profile.</summary>
    public bool StaleProfileDeletable { get; private set; } = true;

    /// <summary>True when this consecutive rebuild count calls for the software mode.</summary>
    public static bool RequiresSoftware(int consecutiveRebuilds) => consecutiveRebuilds >= SoftwareAtRebuild;

    /// <summary>True when the installed runtime major is known and at or past the regression; an unknown runtime stays on GPU.</summary>
    public static bool RuntimeRequiresSoftware(int? runtimeMajor) => runtimeMajor is { } major && major >= SoftwareFromRuntimeMajor;

    /// <summary>Applies the ladder after a rebuild; true when this call switched the mode.</summary>
    public bool OnRebuilt(int consecutiveRebuilds) => RequiresSoftware(consecutiveRebuilds) && ForceSoftware();

    /// <summary>Latches software compositing and persists the marker; true when this call switched the mode.</summary>
    public bool ForceSoftware()
    {
        if (Software) return false;
        Software = true;
        StaleProfileDeletable = false;
        if (_markerPath is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
                File.WriteAllText(_markerPath, _stamp);
            }
            catch (Exception ex) { Log.Warn($"stream-hosts could not persist software compositing: {ex.Message}"); }
        }
        return true;
    }
}
