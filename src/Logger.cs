using System;
using System.IO;

namespace Nexus.Overlay;

/// <summary>
/// File-only logger; no console attached, no UI surface for diagnostics.
/// Trace lands at %ProgramData%\Nexus\logs\nexus-overlay.log, co-located with
/// nexus-service.log. ProgramData (not LocalAppData) because the overlay runs
/// under SYSTEM or the console user: LocalAppData differs between them, and
/// SYSTEM's resolves to C:\Windows\System32\config\systemprofile\AppData\Local,
/// invisible to users. When that file is not writable (the folder holds files
/// created by an elevated process), lines go to %LocalAppData%\Nexus\logs.
/// </summary>
internal static class Log
{
    // Newest archived runs kept; mirrors nexus-service.log's rotation.
    private const int MaxRotatedLogs = 5;

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "logs");
    private static readonly string FallbackDir = UserDataPaths.LogsDir(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    private const string LogFileName = "nexus-overlay.log";
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
                LogFallback.Append(
                    LogDir, FallbackDir, LogFileName,
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
        lock (Sync)
        {
            RotateIn(LogDir);
            RotateIn(FallbackDir);
        }
    }

    private static void RotateIn(string dir)
    {
        try
        {
            var path = Path.Combine(dir, LogFileName);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return;
            var archived = Path.Combine(dir, $"nexus-overlay-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            if (File.Exists(archived)) File.Delete(archived);
            File.Move(path, archived);
            Prune(dir);
        }
        catch { /* best effort */ }
    }

    private static void Prune(string dir)
    {
        try
        {
            // The yyyyMMdd-HHmmss stamp sorts oldest-first by ordinal; drop
            // everything before the last MaxRotatedLogs. Matches nexus-overlay.log,
            // not the live nexus-overlay.log (no dash after the name).
            var rotated = Directory.GetFiles(dir, "nexus-overlay-*.log");
            if (rotated.Length <= MaxRotatedLogs)
                return;
            Array.Sort(rotated, StringComparer.Ordinal);
            for (var i = 0; i < rotated.Length - MaxRotatedLogs; i++)
                File.Delete(rotated[i]);
        }
        catch { /* best effort */ }
    }
}
