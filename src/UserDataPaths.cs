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
    internal static string DesktopProfile() => Profile("DesktopWebView2", ProfileMigration.UiState);

    /// <summary>
    /// The per-user profile folder for a leaf, migrated once from the legacy
    /// ProgramData profile of the same name. Memoized per process.
    /// </summary>
    internal static string Profile(string leaf, ProfileMigration.StateItem[] state)
    {
        lock (Sync)
        {
            if (Resolved.TryGetValue(leaf, out var cached)) return cached;
            var dir = Path.Combine(
                NexusRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), leaf);
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nexus", leaf);
            try { ProfileMigration.Run(legacy, dir, state, Log.Info, OwnedByCurrentUser); }
            catch (Exception ex) { Log.Warn($"profile migration failed: {ex.Message}"); }
            Resolved[leaf] = dir;
            return dir;
        }
    }

    /// <summary>True when the file's owner is the current user. A DPAPI-protected key written by another account (an elevated process) cannot be decrypted here.</summary>
    internal static bool OwnedByCurrentUser(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return true;
            return OwnerMatches(path);
        }
        catch { return false; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool OwnerMatches(string path)
    {
        var owner = new FileInfo(path).GetAccessControl()
            .GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        return owner is not null
            && owner.Equals(System.Security.Principal.WindowsIdentity.GetCurrent().User);
    }
}

/// <summary>One-time copy of the UI state from the legacy ProgramData profile.</summary>
internal static class ProfileMigration
{
    internal const string MarkerName = ".nexus-migrated";

    /// <summary>
    /// One migrated item. <paramref name="After"/> names another item that must have been copied
    /// in the same run; <paramref name="RequiresOwner"/> limits the copy to files owned by the current user.
    /// </summary>
    internal sealed record StateItem(string Rel, string? After = null, bool RequiresOwner = false);

    private static readonly string LocalState = Path.Combine("EBWebView", "Local State");

    // Local Storage holds the dashboard's cached auth token and UI settings;
    // IndexedDB holds app-level stores.
    internal static readonly StateItem[] UiState =
    {
        new(Path.Combine("EBWebView", "Default", "Local Storage")),
        new(Path.Combine("EBWebView", "Default", "IndexedDB")),
    };

    // Embedded-site profiles also carry logins: Network holds the cookie jar and
    // Local State the DPAPI-protected os_crypt key. Cookies copied without the
    // key they were encrypted with are silently dropped, so Network follows
    // Local State and neither is copied for a key another account owns.
    internal static readonly StateItem[] WidgetState =
    {
        new(LocalState, RequiresOwner: true),
        new(Path.Combine("EBWebView", "Default", "Network"), After: LocalState),
        new(Path.Combine("EBWebView", "Default", "Local Storage")),
        new(Path.Combine("EBWebView", "Default", "IndexedDB")),
    };

    /// <summary>
    /// Copies each missing state item from the legacy profile, via a sibling
    /// temp name renamed into place so a failed copy never leaves a half item.
    /// An item that already exists at the destination is never touched, so a
    /// profile the browser has since used is not clobbered; the cost is that
    /// state is lost if the browser created that item before a retry. The
    /// marker is written only when every item was copied or skipped, so any
    /// failure retries on the next launch. Best effort: false on failure.
    /// </summary>
    internal static bool Run(string legacyDir, string newDir, StateItem[] state, Action<string>? log = null,
        Func<string, bool>? ownerOk = null)
    {
        if (File.Exists(Path.Combine(newDir, MarkerName)) || !Directory.Exists(legacyDir)) return false;
        var ok = true;
        var copied = new System.Collections.Generic.HashSet<string>();
        foreach (var item in state)
        {
            var src = Path.Combine(legacyDir, item.Rel);
            var dst = Path.Combine(newDir, item.Rel);
            var temp = dst + ".migrating";
            try { DeleteIfPresent(temp); } catch { }
            if (!(Directory.Exists(src) || File.Exists(src)) || Directory.Exists(dst) || File.Exists(dst)) continue;
            if (item.After is not null && !copied.Contains(item.After)) continue;
            if (item.RequiresOwner && ownerOk is not null && !ownerOk(src)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                Copy(src, temp);
                if (Directory.Exists(temp)) Directory.Move(temp, dst);
                else File.Move(temp, dst);
                copied.Add(item.Rel);
            }
            catch (Exception ex)
            {
                ok = false;
                log?.Invoke($"profile migration of {item.Rel} failed: {ex.Message}");
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
