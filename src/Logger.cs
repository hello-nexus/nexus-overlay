using System;
using System.IO;

namespace Qos.Overlay;

/// <summary>
/// File-only logger. Service spawns us with no console attached and we have
/// no UI surface for diagnostic output, so trace lands at
/// %LOCALAPPDATA%\qOS\desktop-host.log.
/// </summary>
internal static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "qOS", "desktop-host.log");
    private static readonly object Sync = new();

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Warn(string message)
    {
        Write("WARN", message);
    }

    public static void Error(string message)
    {
        Write("ERR ", message);
    }

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
