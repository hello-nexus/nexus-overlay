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
