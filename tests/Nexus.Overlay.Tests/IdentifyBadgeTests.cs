using Xunit;

namespace Nexus.Overlay.Tests;

public class IdentifyBadgeTests
{
    [Theory]
    [InlineData(1920, 1080, 324)]
    [InlineData(1080, 1920, 324)]
    [InlineData(3840, 2160, 648)]
    public void SideIsAFractionOfTheShorterEdge(int w, int h, int expected)
        => Assert.Equal(expected, IdentifyBadge.Side(w, h));

    [Fact]
    public void StartsSmallAndTransparent()
    {
        var (scale, alpha) = IdentifyBadge.Frame(0, 3000);
        Assert.True(scale < 1);
        Assert.Equal(0, alpha);
    }

    [Fact]
    public void ReachesFullSizeAndOpacityAfterTheFadeIn()
    {
        var (scale, alpha) = IdentifyBadge.Frame(IdentifyBadge.FadeInMs, 3000);
        Assert.Equal(1.0, scale, 6);
        Assert.Equal(255, alpha);
    }

    [Fact]
    public void FadesOutAnchoredToTheEnd()
    {
        var (_, mid) = IdentifyBadge.Frame(3000 - IdentifyBadge.FadeOutMs / 2, 3000);
        var (_, end) = IdentifyBadge.Frame(3000, 3000);
        Assert.InRange(mid, 100, 155);
        Assert.Equal(0, end);
    }

    [Fact]
    public void ALateStartShorterThanBothFadesStaysDimmer()
    {
        var (_, alpha) = IdentifyBadge.Frame(100, 400);
        Assert.True(alpha < 255);
    }

    [Theory]
    [InlineData(2999, false)]
    [InlineData(3000, true)]
    public void DoneAtTheTotal(long elapsed, bool done)
        => Assert.Equal(done, IdentifyBadge.IsDone(elapsed, 3000));
}
