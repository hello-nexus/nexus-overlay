using System;
using System.IO;

namespace Nexus.Overlay;

/// <summary>
/// File-only logger. Service spawns us with no console attached and we have
/// no UI surface for diagnostic output, so trace lands at
/// %ProgramData%\Nexus\Logs\desktop-host.log. ProgramData (vs LocalAppData)
/// because the overlay can be spawned under SYSTEM or under the active
/// console user - LocalAppData differs between those, and SYSTEM's resolves
/// to C:\Windows\System32\config\systemprofile\AppData\Local which is
/// invisible to users (same rationale as the WebView2 user-data-dir
/// migration in d7edccd).
/// </summary>
internal static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexus", "Logs", "desktop-host.log");
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
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging never crashes the host */ }
    }

    public static void Reset()
    {
        try
        {
            lock (Sync)
            {
                if (File.Exists(LogPath)) File.Delete(LogPath);
            }
        }
        catch { }
    }
}
