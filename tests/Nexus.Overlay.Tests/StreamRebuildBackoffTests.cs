using System;
using System.IO;
using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public class StreamRebuildBackoffTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 30_000)]
    [InlineData(2, 60_000)]
    [InlineData(3, 120_000)]
    [InlineData(4, 240_000)]
    [InlineData(5, 300_000)]
    [InlineData(1000, 300_000)]
    public void Delay_doubles_and_caps(int rebuilds, long expectedMs)
    {
        Assert.Equal(expectedMs, StreamRebuildBackoff.DelayMs(rebuilds));
    }
}

public class StreamRebuildTrackerTests
{
    [Fact]
    public void A_rebuild_blocks_respawn_until_the_backoff_passes_and_counts_as_pending()
    {
        var t = new StreamRebuildTracker();
        var delay = t.OnRebuilt("s", 1_000);
        Assert.Equal(StreamRebuildBackoff.DelayMs(1), delay);
        Assert.Equal(1, t.PendingCount);
        Assert.True(t.IsBlocked("s", 1_000 + delay - 1));
        Assert.False(t.IsBlocked("s", 1_000 + delay));
        t.OnSpawned("s");
        Assert.Equal(0, t.PendingCount);
    }

    [Fact]
    public void Consecutive_rebuilds_back_off_until_recovered()
    {
        var t = new StreamRebuildTracker();
        Assert.Equal(StreamRebuildBackoff.DelayMs(1), t.OnRebuilt("s", 0));
        t.OnSpawned("s");
        Assert.Equal(StreamRebuildBackoff.DelayMs(2), t.OnRebuilt("s", 0));
        t.OnSpawned("s");
        Assert.True(t.OnRecovered("s"));
        Assert.Equal(StreamRebuildBackoff.DelayMs(1), t.OnRebuilt("s", 0));
    }

    [Fact]
    public void Browser_exits_respawn_at_once_then_back_off_until_recovered()
    {
        var t = new StreamRebuildTracker();
        Assert.Equal(0, t.OnBrowserExited("s", 0));
        Assert.False(t.IsBlocked("s", 0));
        Assert.Equal(0, t.PendingCount);
        Assert.Equal(StreamRebuildBackoff.DelayMs(1), t.OnBrowserExited("s", 0));
        Assert.Equal(1, t.PendingCount);
        Assert.Equal(StreamRebuildBackoff.DelayMs(2), t.OnBrowserExited("s", 0));
        t.OnRecovered("s");
        Assert.Equal(0, t.OnBrowserExited("s", 0));
    }

    [Fact]
    public void Unlock_clears_the_backoff_and_the_pending_wait()
    {
        var t = new StreamRebuildTracker();
        t.OnRebuilt("s", 0);
        t.OnRebuilt("s", 0);
        t.OnUnlocked();
        Assert.False(t.IsBlocked("s", 0));
        Assert.Equal(0, t.PendingCount);
        Assert.Equal(StreamRebuildBackoff.DelayMs(1), t.OnRebuilt("s", 0));
    }

    [Fact]
    public void Prune_drops_sessions_the_service_no_longer_wants()
    {
        var t = new StreamRebuildTracker();
        t.OnRebuilt("keep", 0);
        t.OnRebuilt("gone", 0);
        t.Prune(new[] { "keep" });
        Assert.Equal(1, t.PendingCount);
        Assert.False(t.IsBlocked("gone", 0));
    }
}

public class StreamRenderModeTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Software_compositing_starts_at_the_first_failed_rebuild(int rebuilds, bool software)
    {
        Assert.Equal(software, StreamRenderMode.RequiresSoftware(rebuilds));
    }

    [Fact]
    public void The_tracker_count_feeds_the_ladder_and_resets_on_recovery()
    {
        var t = new StreamRebuildTracker();
        t.OnRebuilt("s", 0);
        Assert.True(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
        t.OnRecovered("s");
        Assert.False(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
    }

    [Fact]
    public void A_recovery_while_the_session_is_locked_keeps_the_rebuild_count()
    {
        var t = new StreamRebuildTracker();
        t.OnRebuilt("s", 0);
        Assert.False(t.OnRecovered("s", sessionLocked: true));
        Assert.Equal(1, t.RebuildCount("s"));
        Assert.True(t.OnRecovered("s", sessionLocked: false));
        Assert.Equal(0, t.RebuildCount("s"));
    }

    [Fact]
    public void Software_mode_latches_and_stays_on()
    {
        var mode = new StreamRenderMode();
        Assert.False(mode.OnRebuilt(0));
        Assert.False(mode.Software);
        Assert.True(mode.OnRebuilt(1));
        Assert.True(mode.Software);
        Assert.False(mode.OnRebuilt(2));
        Assert.False(mode.OnRebuilt(0));
        Assert.True(mode.Software);
        Assert.False(mode.StaleProfileDeletable);
    }

    [Fact]
    public void A_browser_exit_then_one_failed_rebuild_escalates_to_software()
    {
        var t = new StreamRebuildTracker();
        var mode = new StreamRenderMode();
        t.OnBrowserExited("s", 0);
        Assert.True(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
        Assert.True(mode.OnRebuilt(t.RebuildCount("s")));
    }

    [Fact]
    public void The_software_decision_is_persisted_and_the_next_instance_starts_in_software()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-rm-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(dir, "sub", "marker");
        try
        {
            var first = new StreamRenderMode(marker, "v1");
            Assert.False(first.Software);
            Assert.True(first.OnRebuilt(1));
            Assert.True(File.Exists(marker));

            var second = new StreamRenderMode(marker, "v1");
            Assert.True(second.Software);
            Assert.False(second.StaleProfileDeletable);
            Assert.False(second.OnRebuilt(1));

            File.Delete(marker);
            Assert.False(new StreamRenderMode(marker, "v1").Software);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("v1", "v2")]
    [InlineData("", "v1")]
    [InlineData("v1", "")]
    public void A_marker_with_another_stamp_is_deleted_and_not_software(string written, string current)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-rm-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(dir, "marker");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(marker, written);
            var mode = new StreamRenderMode(marker, current);
            Assert.False(mode.Software);
            Assert.True(mode.StaleProfileDeletable);
            Assert.False(File.Exists(marker));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_marker_with_the_same_stamp_keeps_software()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-rm-" + Guid.NewGuid().ToString("N"));
        var marker = Path.Combine(dir, "marker");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(marker, "v1");
            Assert.True(new StreamRenderMode(marker, "v1").Software);
            Assert.True(File.Exists(marker));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void An_empty_marker_path_is_not_persisted()
    {
        var mode = new StreamRenderMode("", "v1");
        Assert.True(mode.OnRebuilt(1));
        Assert.True(mode.Software);
    }
}
