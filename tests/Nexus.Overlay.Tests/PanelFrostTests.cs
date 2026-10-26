using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelFrostTests
{
    private static PanelHostMessage Parse(string json, int w = 2560, int h = 1440) => PanelHostMessage.Parse(json, w, h);

    private static string Msg(string level, string rects, double dpr = 1, double radius = 12)
        => $"{{\"type\":\"nexus.panel.frost\",\"level\":\"{level}\",\"dpr\":{dpr},\"radius\":{radius},\"rects\":[{rects}]}}";

    private const string OneRect = "{\"x\":100,\"y\":50,\"w\":200,\"h\":100}";

    [Fact]
    public void ScalesRectsAndRadiusByDpr()
    {
        var m = Parse(Msg("light", OneRect, dpr: 1.5, radius: 10));
        Assert.Equal(PanelMessageKind.Frost, m.Kind);
        Assert.Equal(FrostLevel.Light, m.Frost!.Level);
        Assert.Equal(15, m.Frost.RadiusPx);
        Assert.Equal(new RegionLayout.Rect(150, 75, 300, 150), Assert.Single(m.Frost.Rects));
    }

    [Fact]
    public void HeavyLevelIsParsed()
        => Assert.Equal(FrostLevel.Heavy, Parse(Msg("heavy", OneRect)).Frost!.Level);

    [Theory]
    [InlineData("none")]
    [InlineData("bogus")]
    [InlineData("")]
    public void NoneAndUnknownLevelsClear(string level)
        => Assert.True(Parse(Msg(level, OneRect)).Frost!.IsEmpty);

    [Fact]
    public void BadDprOrNoRectsClear()
    {
        Assert.True(Parse(Msg("light", OneRect, dpr: 0)).Frost!.IsEmpty);
        Assert.True(Parse(Msg("light", OneRect, dpr: 99)).Frost!.IsEmpty);
        Assert.True(Parse(Msg("light", "")).Frost!.IsEmpty);
        Assert.True(Parse("{\"type\":\"nexus.panel.frost\",\"level\":\"light\",\"dpr\":1}").Frost!.IsEmpty);
    }

    [Fact]
    public void RectsClampToTheWindowAndOffscreenOnesDrop()
    {
        var m = Parse(Msg("light", "{\"x\":-20,\"y\":-20,\"w\":60,\"h\":60},{\"x\":5000,\"y\":0,\"w\":10,\"h\":10}"), 1000, 600);
        Assert.Equal(new RegionLayout.Rect(0, 0, 40, 40), Assert.Single(m.Frost!.Rects));
    }

    [Fact]
    public void PlateCountIsCapped()
    {
        var items = string.Join(",", System.Linq.Enumerable.Range(0, 200).Select(i => $"{{\"x\":{i},\"y\":0,\"w\":1,\"h\":1}}"));
        Assert.Equal(FrostPolicy.MaxPlates, Parse(Msg("light", items)).Frost!.Rects.Count);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(1e9, 256)]
    [InlineData(double.NaN, 0)]
    [InlineData(11.6, 12)]
    public void RadiusIsClamped(double input, int expected) => Assert.Equal(expected, FrostPolicy.ClampRadius(input));

    [Theory]
    [InlineData(40, 100, 50, 25)]
    [InlineData(10, 100, 50, 10)]
    [InlineData(0, 100, 50, 0)]
    [InlineData(40, 0, 50, 0)]
    public void PlateRadiusNeverExceedsHalfTheShorterSide(int radius, int w, int h, int expected)
        => Assert.Equal(expected, FrostPolicy.RadiusFor(radius, w, h));

    [Theory]
    [InlineData(19045, false)]
    [InlineData(21999, false)]
    [InlineData(22000, true)]
    [InlineData(26100, true)]
    public void OnlyWindows11IsSupported(int build, bool expected) => Assert.Equal(expected, FrostPolicy.IsSupportedBuild(build));

    [Fact]
    public void HeavyTintIsMoreOpaqueThanLight()
    {
        Assert.True((FrostPolicy.TintFor(FrostLevel.Heavy) >> 24) > (FrostPolicy.TintFor(FrostLevel.Light) >> 24));
        Assert.Equal(0u, FrostPolicy.TintFor(FrostLevel.None));
    }

    private static FrostPlan Plan(FrostLevel level, int radius, params (int x, int w)[] cards)
        => new(level, radius, cards.Select(c => new RegionLayout.Rect(c.x, 0, c.w, 10)).ToList());

    [Fact]
    public void UnchangedPlanIsANoop()
        => Assert.True(FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5), (10, 5)), Plan(FrostLevel.Light, 8, (0, 5), (10, 5))).IsNoop);

    [Fact]
    public void MovedCardReplacesOnlyThatPlate()
    {
        var d = FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5), (10, 5)), Plan(FrostLevel.Light, 8, (0, 5), (12, 5)));
        Assert.Equal(new[] { 1 }, d.Place);
        Assert.Equal((0, 0, false), (d.Create, d.Destroy, d.Retint));
    }

    [Fact]
    public void GrowingCreatesAndShrinkingDestroysFromTheEnd()
    {
        var grow = FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5)), Plan(FrostLevel.Light, 8, (0, 5), (10, 5), (20, 5)));
        Assert.Equal(2, grow.Create);
        Assert.Equal(new[] { 1, 2 }, grow.Place);
        var shrink = FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5), (10, 5), (20, 5)), Plan(FrostLevel.Light, 8, (0, 5)));
        Assert.Equal(2, shrink.Destroy);
        Assert.Empty(shrink.Place);
    }

    [Fact]
    public void LevelOrRadiusChangeReplacesEveryPlateAndLevelRetints()
    {
        var level = FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5), (10, 5)), Plan(FrostLevel.Heavy, 8, (0, 5), (10, 5)));
        Assert.Equal(new[] { 0, 1 }, level.Place);
        Assert.True(level.Retint);
        var radius = FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5)), Plan(FrostLevel.Light, 9, (0, 5)));
        Assert.Equal(new[] { 0 }, radius.Place);
        Assert.False(radius.Retint);
    }

    [Fact]
    public void ClearingDestroysAllAndFromEmptyCreatesAll()
    {
        var clear = FrostPolicy.Diff(Plan(FrostLevel.Light, 8, (0, 5), (10, 5)), FrostPlan.Empty);
        Assert.Equal(2, clear.Destroy);
        var fresh = FrostPolicy.Diff(FrostPlan.Empty, Plan(FrostLevel.Heavy, 8, (0, 5)));
        Assert.Equal(1, fresh.Create);
        Assert.Equal(new[] { 0 }, fresh.Place);
        Assert.True(FrostPolicy.Diff(FrostPlan.Empty, FrostPlan.Empty).IsNoop);
    }
}
