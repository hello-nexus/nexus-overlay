using System;
using System.IO;

namespace Nexus.Overlay;

/// <summary>
/// File-only logger; no console attached, no UI surface for diagnostics.
/// Trace lands at %ProgramData%\Nexus\logs\nexus-overlay.log, co-located with
/// nexus-service.log. ProgramData (not LocalAppData) because the overlay runs
/// under SYSTEM or the console user: LocalAppData differs between them, and
/// SYSTEM's resolves to C:\Windows\System32\config\systemprofile\AppData\Local,
/// invisible to users.
/// </summary>
internal static class Log
{
    // Newest archived runs kept; mirrors nexus-service.log's rotation.
    private const int MaxRotatedLogs = 5;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "logs");
    private static readonly string LogPath = Path.Combine(LogDir, "nexus-overlay.log");
    private static readonly object Sync = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging never crashes the host */ }
    }

    /// <summary>
    /// Archive the previous run to nexus-overlay-&lt;timestamp&gt;.log instead of
    /// deleting it, so a crash that triggers a respawn leaves its log behind.
    /// Called once on a winning overlay start. Keeps the newest MaxRotatedLogs.
    /// </summary>
    public static void Rotate()
    {
        try
        {
            lock (Sync)
            {
                if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
                    return;
                Directory.CreateDirectory(LogDir);
                var archived = Path.Combine(LogDir, $"nexus-overlay-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                if (File.Exists(archived)) File.Delete(archived);
                File.Move(LogPath, archived);
                Prune();
            }
        }
        catch { /* best effort */ }
    }

    private static void Prune()
    {
        try
        {
            // The yyyyMMdd-HHmmss stamp sorts oldest-first by ordinal; drop
            // everything before the last MaxRotatedLogs. Matches nexus-overlay.log,
            // not the live nexus-overlay.log (no dash after the name).
            var rotated = Directory.GetFiles(LogDir, "nexus-overlay-*.log");
            if (rotated.Length <= MaxRotatedLogs)
                return;
            Array.Sort(rotated, StringComparer.Ordinal);
            for (var i = 0; i < rotated.Length - MaxRotatedLogs; i++)
                File.Delete(rotated[i]);
        }
        catch { /* best effort */ }
    }
}
