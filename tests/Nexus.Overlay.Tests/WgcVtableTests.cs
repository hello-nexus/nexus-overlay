using System;
using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public class WgcVtableTests
{
    [Fact]
    public void Session5_ids_match_the_windows_projection_metadata()
    {
        Assert.Equal(new Guid("67C0EA62-1F85-5061-925A-239BE0AC09CB"), WgcVtable.IID_IGraphicsCaptureSession5);
        Assert.Equal(6, WgcVtable.Session5_get_MinUpdateInterval);
        Assert.Equal(7, WgcVtable.Session5_put_MinUpdateInterval);
    }

    [Fact]
    public void Capture_interval_is_below_the_shortest_display_tick_the_stream_rates_use()
    {
        // 100 ns ticks; the bound is the shortest display tick the stream rates use.
        Assert.InRange(WgcVtable.CaptureMinUpdateIntervalTicks, 1, 41_000);
    }
}
