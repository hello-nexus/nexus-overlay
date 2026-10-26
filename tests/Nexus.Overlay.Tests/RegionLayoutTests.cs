using System.Text.Json;
using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class RegionLayoutTests
{
    private static bool Read(string json, out double x, out double y, out double w, out double h)
    {
        using var doc = JsonDocument.Parse(json);
        return RegionLayout.TryReadRectF(doc.RootElement, out x, out y, out w, out h);
    }

    [Fact]
    public void KeepsFractionalValues()
    {
        Assert.True(Read("{\"x\":1.25,\"y\":2.5,\"w\":3.75,\"h\":4}", out var x, out var y, out var w, out var h));
        Assert.Equal((1.25, 2.5, 3.75, 4.0), (x, y, w, h));
    }

    [Theory]
    [InlineData("{\"x\":0,\"y\":0,\"w\":0,\"h\":5}")]
    [InlineData("{\"x\":0,\"y\":0,\"w\":5,\"h\":-1}")]
    [InlineData("{\"x\":0,\"y\":0,\"w\":5}")]
    [InlineData("{\"x\":\"0\",\"y\":0,\"w\":5,\"h\":5}")]
    [InlineData("[]")]
    [InlineData("{\"x\":1e999,\"y\":0,\"w\":5,\"h\":5}")]
    public void RejectsUnusableRects(string json) => Assert.False(Read(json, out _, out _, out _, out _));
}
