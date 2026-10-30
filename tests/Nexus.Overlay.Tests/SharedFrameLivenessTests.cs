using Nexus.Overlay.Media;
using Xunit;

namespace Nexus.Overlay.Tests;

public class SharedFrameLivenessTests
{
    [Fact]
    public void A_reader_that_keeps_consuming_is_never_stale()
    {
        var live = new SharedFrameLiveness();
        long written = 0;
        for (long now = 0; now < 60_000; now += 33)
        {
            written += 2;
            Assert.False(live.IsStale(now, written, written - 2));
        }
    }

    [Fact]
    public void A_reader_stuck_while_frames_keep_coming_goes_stale_after_the_window()
    {
        var live = new SharedFrameLiveness();
        Assert.False(live.IsStale(0, 2, 0));
        long written = 2;
        for (long now = 100; now <= SharedFrameLiveness.StaleAfterMs; now += 100)
        {
            written += 2;
            Assert.False(live.IsStale(now, written, 0));
        }
        Assert.True(live.IsStale(SharedFrameLiveness.StaleAfterMs + 1, written + 2, 0));
    }

    [Fact]
    public void Progress_restarts_the_window()
    {
        var live = new SharedFrameLiveness();
        Assert.False(live.IsStale(0, 2, 0));
        Assert.False(live.IsStale(4000, 10, 0));
        Assert.False(live.IsStale(4100, 12, 8));
        Assert.False(live.IsStale(9000, 20, 8));
        Assert.True(live.IsStale(9200, 22, 8));
    }

    [Fact]
    public void Nothing_outstanding_is_never_stale()
    {
        var live = new SharedFrameLiveness();
        Assert.False(live.IsStale(0, 10, 10));
        Assert.False(live.IsStale(60_000, 10, 10));
    }
}
