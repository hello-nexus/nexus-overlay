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

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    public void ShouldMigrate_only_when_destination_missing_and_legacy_present(bool dest, bool legacy, bool expected)
        => Assert.Equal(expected, ProfileMigration.ShouldMigrate(dest, legacy));

    [Fact]
    public void LogsDir_is_under_the_nexus_root()
        => Assert.Equal(Path.Combine("x", "Nexus", "logs"), UserDataPaths.LogsDir("x"));

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

        Assert.True(ProfileMigration.Run(legacy, dest));

        Assert.Equal("token", File.ReadAllText(Path.Combine(dest, ls, "000003.log")));
        Assert.Equal("idb", File.ReadAllText(Path.Combine(dest, "EBWebView", "Default", "IndexedDB", "a", "1.ldb")));
        Assert.False(File.Exists(Path.Combine(dest, ls, "LOCK")));
        Assert.False(Directory.Exists(Path.Combine(dest, "EBWebView", "Default", "Cache")));
        Assert.False(Directory.Exists(dest + ".migrating"));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void Migration_leaves_an_existing_destination_alone()
    {
        var legacy = Path.Combine(_root, "legacy");
        var dest = Path.Combine(_root, "new");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Local Storage", "a"), "old");
        Write(Path.Combine(dest, "keep"), "mine");

        Assert.False(ProfileMigration.Run(legacy, dest));
        Assert.False(File.Exists(Path.Combine(dest, "EBWebView", "Default", "Local Storage", "a")));
    }

    [Fact]
    public void Migration_without_legacy_creates_nothing()
    {
        var dest = Path.Combine(_root, "new");
        Assert.False(ProfileMigration.Run(Path.Combine(_root, "none"), dest));
        Assert.False(Directory.Exists(dest));
    }

    [Fact]
    public void Migration_failure_does_not_throw_or_leave_a_half_profile()
    {
        var legacy = Path.Combine(_root, "legacy");
        Write(Path.Combine(legacy, "EBWebView", "Default", "Local Storage", "a"), "x");
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var dest = Path.Combine(blocker, "new");

        Assert.False(ProfileMigration.Run(legacy, dest));
        Assert.False(Directory.Exists(dest));
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
}
