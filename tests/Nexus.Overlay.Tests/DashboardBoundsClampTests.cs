using Xunit;

namespace Nexus.Overlay.Tests;

public class DashboardBoundsClampTests
{
    private const int Fx = 8;
    private const int Fy = 8;

    [Fact]
    public void NormalRectIsUntouched()
    {
        var r = DashboardBoundsClamp.Clamp(100, 100, 1200, 800, 0, 0, 1920, 1040, Fx, Fy);
        Assert.Equal((100, 100, 1200, 800), r);
    }

    [Fact]
    public void OversizedRectIsCappedToWorkArea()
    {
        var r = DashboardBoundsClamp.Clamp(-1569, 80, 3232, 1978, 0, 0, 1920, 1040, Fx, Fy);
        Assert.Equal(1920 + 2 * Fx, r.w);
        Assert.Equal(1040 + Fy, r.h);
        Assert.Equal(-Fx, r.x);
        Assert.Equal(0, r.y);
    }

    [Fact]
    public void NegativePositionIsClampedInside()
    {
        var r = DashboardBoundsClamp.Clamp(-500, -300, 1200, 800, 0, 0, 1920, 1040, Fx, Fy);
        Assert.Equal((-Fx, 0, 1200, 800), r);
    }

    [Fact]
    public void RectPastBottomRightIsPulledBack()
    {
        var r = DashboardBoundsClamp.Clamp(1500, 900, 1200, 800, 0, 0, 1920, 1040, Fx, Fy);
        Assert.Equal(1920 - (1200 - 2 * Fx) - Fx, r.x);
        Assert.Equal(1040 - (800 - Fy), r.y);
        Assert.Equal((1200, 800), (r.w, r.h));
    }
}
