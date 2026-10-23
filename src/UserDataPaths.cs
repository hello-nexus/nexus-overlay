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
    private static readonly object Sync = new();
    private static readonly System.Collections.Generic.Dictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

    internal static string NexusRoot(string localAppData) => Path.Combine(localAppData, "Nexus");

    internal static string LogsDir(string localAppData) => Path.Combine(NexusRoot(localAppData), "logs");

    /// <summary>
    /// The shared profile folder for the dashboard, overlay and kiosk surfaces.
    /// Every surface must resolve it here so they keep sharing one browser
    /// process tree.
    /// </summary>
    internal static string DesktopProfile() => Profile("DesktopWebView2");

    /// <summary>
    /// The per-user profile folder for a leaf, migrated once from the legacy
    /// ProgramData profile of the same name. Memoized per process.
    /// </summary>
    internal static string Profile(string leaf)
    {
        lock (Sync)
        {
            if (Resolved.TryGetValue(leaf, out var cached)) return cached;
            var dir = Path.Combine(
                NexusRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), leaf);
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", leaf);
            try { ProfileMigration.Run(legacy, dir, ProfileMigration.StateFor(leaf), Log.Info); }
            catch (Exception ex) { Log.Warn($"profile migration failed: {ex.Message}"); }
            Resolved[leaf] = dir;
            return dir;
        }
    }
}

/// <summary>One-time copy of the UI state from the legacy ProgramData profile.</summary>
internal static class ProfileMigration
{
    internal const string MarkerName = ".nexus-migrated";

    // Local Storage holds the dashboard's cached auth token and UI settings;
    // IndexedDB holds app-level stores.
    private static readonly string[] UiState =
    {
        Path.Combine("EBWebView", "Default", "Local Storage"),
        Path.Combine("EBWebView", "Default", "IndexedDB"),
    };

    // Embedded-site profiles also carry logins: Network holds the cookie jar
    // and Local State the DPAPI-protected os_crypt key, without which the
    // browser silently drops the encrypted cookies.
    private static readonly string[] WidgetState =
    {
        Path.Combine("EBWebView", "Local State"),
        Path.Combine("EBWebView", "Default", "Network"),
        Path.Combine("EBWebView", "Default", "Local Storage"),
        Path.Combine("EBWebView", "Default", "IndexedDB"),
    };

    internal static string[] StateFor(string leaf) =>
        leaf.Contains("Widget", StringComparison.OrdinalIgnoreCase) ? WidgetState : UiState;

    /// <summary>
    /// Copies each missing state item from the legacy profile, via a sibling
    /// temp name renamed into place so a failed copy never leaves a half item.
    /// An item that already exists at the destination is never touched, so a
    /// profile the browser has since used is not clobbered; the cost is that
    /// state is lost if the browser created that item before a retry. The
    /// marker is written only when every item was copied or skipped, so any
    /// failure retries on the next launch. Best effort: false on failure.
    /// </summary>
    internal static bool Run(string legacyDir, string newDir, string[] state, Action<string>? log = null)
    {
        if (File.Exists(Path.Combine(newDir, MarkerName)) || !Directory.Exists(legacyDir)) return false;
        var ok = true;
        foreach (var rel in state)
        {
            var src = Path.Combine(legacyDir, rel);
            var dst = Path.Combine(newDir, rel);
            if (!(Directory.Exists(src) || File.Exists(src)) || Directory.Exists(dst) || File.Exists(dst)) continue;
            var temp = dst + ".migrating";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                DeleteIfPresent(temp);
                Copy(src, temp);
                if (Directory.Exists(temp)) Directory.Move(temp, dst);
                else File.Move(temp, dst);
            }
            catch (Exception ex)
            {
                ok = false;
                log?.Invoke($"profile migration of {rel} failed: {ex.Message}");
                try { DeleteIfPresent(temp); } catch { }
            }
        }
        if (!ok) return false;
        try
        {
            Directory.CreateDirectory(newDir);
            File.WriteAllText(Path.Combine(newDir, MarkerName), "");
            log?.Invoke("migrated profile state to the per-user folder");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"profile migration marker failed: {ex.Message}");
            return false;
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }

    private static void Copy(string src, string dst)
    {
        if (File.Exists(src)) { CopyFile(src, dst); return; }
        CopyTree(src, dst);
    }

    private static void CopyFile(string file, string target)
    {
        using var from = new FileStream(file, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var to = new FileStream(target, FileMode.Create, FileAccess.Write);
        from.CopyTo(to);
    }

    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            // The leveldb LOCK is recreated by the browser; copying a held one fails.
            if (string.Equals(Path.GetFileName(file), "LOCK", StringComparison.OrdinalIgnoreCase)) continue;
            CopyFile(file, Path.Combine(dst, Path.GetFileName(file)));
        }
        foreach (var sub in Directory.EnumerateDirectories(src))
            CopyTree(sub, Path.Combine(dst, Path.GetFileName(sub)));
    }
}

/// <summary>Append to a log under the shared dir, falling back to the per-user dir when that is not writable.</summary>
internal static class LogFallback
{
    // Dirs whose write already failed this process: later lines skip the doomed attempt.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Failed =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Append(string primaryDir, string fallbackDir, string fileName, string text)
    {
        if (!Failed.ContainsKey(primaryDir))
        {
            try
            {
                Directory.CreateDirectory(primaryDir);
                File.AppendAllText(Path.Combine(primaryDir, fileName), text);
                return;
            }
            catch { Failed[primaryDir] = true; }
        }
        Directory.CreateDirectory(fallbackDir);
        File.AppendAllText(Path.Combine(fallbackDir, fileName), text);
    }
}
