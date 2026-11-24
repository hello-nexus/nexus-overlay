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
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(5, true)]
    public void Software_compositing_starts_at_the_second_consecutive_rebuild(int rebuilds, bool software)
    {
        Assert.Equal(software, StreamRenderMode.RequiresSoftware(rebuilds));
    }

    [Fact]
    public void The_tracker_count_feeds_the_ladder_and_resets_on_recovery()
    {
        var t = new StreamRebuildTracker();
        t.OnRebuilt("s", 0);
        Assert.False(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
        t.OnRebuilt("s", 0);
        Assert.True(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
        t.OnRecovered("s");
        Assert.False(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
    }

    [Fact]
    public void Software_mode_latches_through_lower_counts_and_clears_on_reset()
    {
        var mode = new StreamRenderMode();
        Assert.False(mode.OnRebuilt(1));
        Assert.False(mode.Software);
        Assert.True(mode.OnRebuilt(2));
        Assert.True(mode.Software);
        Assert.False(mode.OnRebuilt(3));
        Assert.False(mode.OnRebuilt(1));
        Assert.True(mode.Software);
        mode.Reset();
        Assert.False(mode.Software);
    }

    [Fact]
    public void A_browser_exit_then_one_failed_rebuild_escalates_to_software()
    {
        var t = new StreamRebuildTracker();
        var mode = new StreamRenderMode();
        t.OnBrowserExited("s", 0);
        Assert.False(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
        t.OnRebuilt("s", 0);
        Assert.True(StreamRenderMode.RequiresSoftware(t.RebuildCount("s")));
        Assert.True(mode.OnRebuilt(t.RebuildCount("s")));
    }
}
