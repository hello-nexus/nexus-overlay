using System;
using System.IO;

namespace Nexus.Overlay;

/// <summary>
/// Per-user data locations. WebView2 profiles live under the user's own
/// LocalAppData: %ProgramData% only grants a normal user write access to files
/// that user created, so one file created by an elevated or SYSTEM process
/// (an admin run, an elevated restore) makes the whole profile unwritable and
/// every window open stalls on leveldb lock retries.
/// </summary>
internal static class UserDataPaths
{
    internal const string DesktopProfileLeaf = "DesktopWebView2";

    private static readonly object MigrationSync = new();
    private static string? _desktopProfile;

    internal static string NexusRoot(string localAppData) => Path.Combine(localAppData, "Nexus");

    internal static string LogsDir(string localAppData) => Path.Combine(NexusRoot(localAppData), "logs");

    /// <summary>
    /// The shared profile folder for the dashboard, overlay and kiosk surfaces,
    /// migrated from the legacy ProgramData profile on first use. Every surface
    /// must resolve it here so they keep sharing one browser process tree.
    /// </summary>
    internal static string DesktopProfile()
    {
        lock (MigrationSync)
        {
            if (_desktopProfile is not null) return _desktopProfile;
            var dir = Path.Combine(
                NexusRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                DesktopProfileLeaf);
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Nexus", DesktopProfileLeaf);
            try { ProfileMigration.Run(legacy, dir, Log.Info); }
            catch (Exception ex) { Log.Warn($"profile migration failed: {ex.Message}"); }
            _desktopProfile = dir;
            return dir;
        }
    }

    /// <summary>Per-user folder for a surface that never shared the desktop profile.</summary>
    internal static string UserProfile(string leaf) => Path.Combine(
        NexusRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), leaf);
}

/// <summary>One-time copy of the UI state from the legacy ProgramData profile.</summary>
internal static class ProfileMigration
{
    // Relative to the user-data-dir. Local Storage holds the dashboard's cached
    // auth token and UI settings; IndexedDB holds app-level stores.
    private static readonly string[] StateDirs =
    {
        Path.Combine("EBWebView", "Default", "Local Storage"),
        Path.Combine("EBWebView", "Default", "IndexedDB"),
    };

    /// <summary>True when a copy is warranted: nothing at the destination, a profile at the source.</summary>
    internal static bool ShouldMigrate(bool destinationExists, bool legacyExists) =>
        !destinationExists && legacyExists;

    /// <summary>
    /// Copies the state folders into a sibling temp folder, then renames it into
    /// place so a failed copy never leaves a half profile that later launches
    /// would treat as migrated. Best effort: returns false on any failure.
    /// </summary>
    internal static bool Run(string legacyDir, string newDir, Action<string>? log = null)
    {
        if (!ShouldMigrate(Directory.Exists(newDir), Directory.Exists(legacyDir))) return false;
        var temp = newDir + ".migrating";
        try
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            Directory.CreateDirectory(temp);
            foreach (var rel in StateDirs)
            {
                var src = Path.Combine(legacyDir, rel);
                if (Directory.Exists(src)) CopyTree(src, Path.Combine(temp, rel));
            }
            Directory.Move(temp, newDir);
            log?.Invoke("migrated desktop profile state to the per-user folder");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"desktop profile migration skipped: {ex.Message}");
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch { }
            return false;
        }
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            // The leveldb LOCK is recreated by the browser; copying a held one fails.
            if (string.Equals(Path.GetFileName(file), "LOCK", StringComparison.OrdinalIgnoreCase)) continue;
            using var from = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var to = new FileStream(Path.Combine(dst, Path.GetFileName(file)), FileMode.Create, FileAccess.Write);
            from.CopyTo(to);
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyTree(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }
}

/// <summary>Append to a log under the shared dir, falling back to the per-user dir when that is not writable.</summary>
internal static class LogFallback
{
    internal static void Append(string primaryDir, string fallbackDir, string fileName, string text)
    {
        try
        {
            Directory.CreateDirectory(primaryDir);
            File.AppendAllText(Path.Combine(primaryDir, fileName), text);
            return;
        }
        catch { }
        Directory.CreateDirectory(fallbackDir);
        File.AppendAllText(Path.Combine(fallbackDir, fileName), text);
    }
}
