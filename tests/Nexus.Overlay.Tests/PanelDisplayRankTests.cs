using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelDisplayRankTests
{
    [Fact]
    public void GtScreenOutranksYTouchPanel()
    {
        var gt = PanelDisplay.PanelRank(@"MONITOR\RTK2234\{id}");
        var touch = PanelDisplay.PanelRank(@"MONITOR\RTK0004\{id}");
        Assert.True(gt >= 0);
        Assert.True(gt < touch);
    }

    [Theory]
    [InlineData(@"MONITOR\DELA0B8\{id}")]
    [InlineData("")]
    public void UnknownDisplayDoesNotMatch(string id) => Assert.Equal(-1, PanelDisplay.PanelRank(id));

    [Fact]
    public void MatchIsCaseInsensitive() => Assert.True(PanelDisplay.PanelRank(@"monitor\rtk0004\x") >= 0);
}
