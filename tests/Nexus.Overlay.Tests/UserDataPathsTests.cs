using System.IO;
using Xunit;

namespace Nexus.Overlay.Tests;

public sealed class UserDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-udp-" + Guid.NewGuid().ToString("N"));

    public UserDataPathsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static readonly string[] Ui = ProfileMigration.StateFor("DesktopWebView2");
    private static readonly string[] Widget = ProfileMigration.StateFor("WidgetWebView2");

    [Fact]
    public void LogsDir_is_under_the_nexus_root()
        => Assert.Equal(Path.Combine("x", "Nexus", "logs"), UserDataPaths.LogsDir("x"));

    [Theory]
    [InlineData("DesktopWebView2", false)]
    [InlineData("PanelCompatWebView2", false)]
    [InlineData("StreamWebView2", false)]
    [InlineData("WidgetWebView2", true)]
    [InlineData("PanelCompatWidgetWebView2", true)]
    public void Widget_profiles_also_carry_cookies_and_the_crypt_key(string leaf, bool widget)
    {
        var state = ProfileMigration.StateFor(leaf);
        Assert.Equal(widget, state.Contains(Path.Combine("EBWebView", "Local State")));
        Assert.Equal(widget, state.Contains(Path.Combine("EBWebView", "Default", "Network")));
        Assert.Contains(Path.Combine("EBWebView", "Default", "Local Storage"), state);
        Assert.Contains(Path.Combine("EBWebView", "Default", "IndexedDB"), state);
    }

    [Fact]
    public void Migration_copies_state_dirs_and_skips_lock_and_caches()
    {
        var legacy = Path.Combine(_root, "legacy");
        var dest = Path.Combine(_root, "new");
        var ls = Path.Combine("EBWebView", "Default", "Local Storage", "leveldb");
        Write(Path.Combine(legacy, ls, "000003.log"), "token");
        Write(Path.Combine(legacy, ls, "LOCK"), "");
        Write(Path.Combine(legacy, "EBWebView", "Default", "IndexedDB", "a", "1.ldb"), "idb");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Cache", "x"), "cache");

        Assert.True(ProfileMigration.Run(legacy, dest, Ui));

        Assert.Equal("token", File.ReadAllText(Path.Combine(dest, ls, "000003.log")));
        Assert.Equal("idb", File.ReadAllText(Path.Combine(dest, "EBWebView", "Default", "IndexedDB", "a", "1.ldb")));
        Assert.False(File.Exists(Path.Combine(dest, ls, "LOCK")));
        Assert.False(Directory.Exists(Path.Combine(dest, "EBWebView", "Default", "Cache")));
        Assert.True(File.Exists(Path.Combine(dest, ProfileMigration.MarkerName)));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void Widget_migration_copies_local_state_file_and_network_cookies()
    {
        var legacy = Path.Combine(_root, "legacy");
        var dest = Path.Combine(_root, "new");
        Write(Path.Combine(legacy, "EBWebView", "Local State"), "crypt-key");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Network", "Cookies"), "jar");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Network", "Cookies-journal"), "j");

        Assert.True(ProfileMigration.Run(legacy, dest, Widget));

        Assert.Equal("crypt-key", File.ReadAllText(Path.Combine(dest, "EBWebView", "Local State")));
        Assert.Equal("jar", File.ReadAllText(Path.Combine(dest, "EBWebView", "Default", "Network", "Cookies")));
        Assert.True(File.Exists(Path.Combine(dest, "EBWebView", "Default", "Network", "Cookies-journal")));
    }

    [Fact]
    public void Migration_does_nothing_once_the_marker_exists()
    {
        var legacy = Path.Combine(_root, "legacy");
        var dest = Path.Combine(_root, "new");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Local Storage", "a"), "old");
        Write(Path.Combine(dest, ProfileMigration.MarkerName), "");

        Assert.False(ProfileMigration.Run(legacy, dest, Ui));
        Assert.False(Directory.Exists(Path.Combine(dest, "EBWebView")));
    }

    [Fact]
    public void Migration_retries_when_the_destination_dir_exists_without_marker_but_never_overwrites_items()
    {
        var legacy = Path.Combine(_root, "legacy");
        var dest = Path.Combine(_root, "new");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Local Storage", "a"), "old-ls");
        Write(Path.Combine(legacy, "EBWebView", "Default", "IndexedDB", "b"), "old-idb");
        // The browser already created Local Storage here; the caller made the dir after a failed first attempt.
        Write(Path.Combine(dest, "EBWebView", "Default", "Local Storage", "a"), "live");

        Assert.True(ProfileMigration.Run(legacy, dest, Ui));

        Assert.Equal("live", File.ReadAllText(Path.Combine(dest, "EBWebView", "Default", "Local Storage", "a")));
        Assert.Equal("old-idb", File.ReadAllText(Path.Combine(dest, "EBWebView", "Default", "IndexedDB", "b")));
        Assert.True(File.Exists(Path.Combine(dest, ProfileMigration.MarkerName)));
    }

    [Fact]
    public void Migration_without_legacy_creates_nothing()
    {
        var dest = Path.Combine(_root, "new");
        Assert.False(ProfileMigration.Run(Path.Combine(_root, "none"), dest, Ui));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void Migration_failure_leaves_no_marker_so_the_next_launch_retries()
    {
        var legacy = Path.Combine(_root, "legacy");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Local Storage", "a"), "x");
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var dest = Path.Combine(blocker, "new");

        Assert.False(ProfileMigration.Run(legacy, dest, Ui));
        Assert.False(Directory.Exists(dest));

        // Once the obstacle is gone the same call succeeds.
        File.Delete(blocker);
        Assert.True(ProfileMigration.Run(legacy, dest, Ui));
        Assert.Equal("x", File.ReadAllText(Path.Combine(dest, "EBWebView", "Default", "Local Storage", "a")));
    }

    [Fact]
    public void LogFallback_uses_primary_when_writable()
    {
        var primary = Path.Combine(_root, "p");
        var fallback = Path.Combine(_root, "f");
        LogFallback.Append(primary, fallback, "o.log", "a\n");
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(primary, "o.log")));
        Assert.False(Directory.Exists(fallback));
    }

    [Fact]
    public void LogFallback_uses_fallback_when_primary_is_unwritable()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var fallback = Path.Combine(_root, "f");
        LogFallback.Append(Path.Combine(blocker, "logs"), fallback, "o.log", "a\n");
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(fallback, "o.log")));
    }

    [Fact]
    public void LogFallback_stops_retrying_a_failed_primary_for_the_process()
    {
        var primary = Path.Combine(_root, "later");
        var blocker = Path.Combine(_root, "blocker2");
        File.WriteAllText(blocker, "");
        var bad = Path.Combine(blocker, "logs");
        var fallback = Path.Combine(_root, "f2");
        LogFallback.Append(bad, fallback, "o.log", "1\n");
        File.Delete(blocker);
        LogFallback.Append(bad, fallback, "o.log", "2\n");
        Assert.Equal("1\n2\n", File.ReadAllText(Path.Combine(fallback, "o.log")));
        Assert.False(Directory.Exists(bad));
    }
}
