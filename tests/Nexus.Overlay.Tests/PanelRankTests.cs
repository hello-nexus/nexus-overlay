using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelRankTests
{
    [Fact]
    public void GtScreenOutranksYTouchPanel()
    {
        var gt = PanelRank.Of(@"MONITOR\RTK2234\{id}");
        var touch = PanelRank.Of(@"MONITOR\RTK0004\{id}");
        Assert.True(gt >= 0);
        Assert.True(gt < touch);
    }

    [Theory]
    [InlineData(@"MONITOR\DELA0B8\{id}")]
    [InlineData("")]
    public void UnknownDisplayDoesNotMatch(string id) => Assert.Equal(-1, PanelRank.Of(id));

    [Fact]
    public void MatchIsCaseInsensitive() => Assert.True(PanelRank.Of(@"monitor\rtk0004\x") >= 0);
}
