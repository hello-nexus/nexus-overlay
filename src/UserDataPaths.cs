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
            try { ProfileMigration.Run(legacy, dir, state, Log.Info, ProfileMigration.CanDecryptCryptKey); }
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

    /// <summary>
    /// One migrated item. <paramref name="After"/> names another item that must have been copied
    /// in the same run; <paramref name="RequiresDecryptableKey"/> limits the copy to a Local State whose os_crypt key
    /// the current user can decrypt.
    /// </summary>
    internal sealed record StateItem(string Rel, string? After = null, bool RequiresDecryptableKey = false);

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
    // Local State, and neither is copied unless the current user can decrypt
    // that key (a key made under another Windows account cannot be).
    internal static readonly StateItem[] WidgetState =
    {
        new(LocalState, RequiresDecryptableKey: true),
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
        Func<string, bool>? keyOk = null)
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
            if (item.RequiresDecryptableKey && keyOk is not null && !keyOk(src)) continue;
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

    /// <summary>The raw DPAPI blob of Chromium's os_crypt.encrypted_key in a Local State document, or null.</summary>
    internal static byte[]? ExtractEncryptedKey(string localStateJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(localStateJson);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var crypt)
                || !crypt.TryGetProperty("encrypted_key", out var key)
                || key.GetString() is not { } b64)
                return null;
            var raw = Convert.FromBase64String(b64);
            var prefix = System.Text.Encoding.ASCII.GetBytes("DPAPI");
            if (raw.Length <= prefix.Length || !raw.AsSpan(0, prefix.Length).SequenceEqual(prefix)) return null;
            return raw[prefix.Length..];
        }
        catch { return null; }
    }

    /// <summary>True when the current Windows user can DPAPI-decrypt the key in the given Local State file. Fails safe to false.</summary>
    internal static bool CanDecryptCryptKey(string localStatePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string json;
            using (var fs = new FileStream(localStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs))
                json = reader.ReadToEnd();
            var blob = ExtractEncryptedKey(json);
            return blob is not null && Unprotects(blob);
        }
        catch { return false; }
    }

    private static bool Unprotects(byte[] blob)
    {
        var input = new DataBlob { cbData = (uint)blob.Length, pbData = System.Runtime.InteropServices.Marshal.AllocHGlobal(blob.Length) };
        var output = default(DataBlob);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(blob, 0, input.pbData, blob.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)) return false;
            return true;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero)
            {
                // The output is the decrypted key: wipe it before releasing.
                System.Runtime.InteropServices.Marshal.Copy(new byte[output.cbData], 0, output.pbData, (int)output.cbData);
                LocalFree(output.pbData);
            }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DataBlob
    {
        public uint cbData;
        public IntPtr pbData;
    }

    // CRYPTPROTECT_UI_FORBIDDEN (1): never prompt.
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

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
