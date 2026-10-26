using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelHostMessageTests
{
    private static PanelHostMessage Parse(string json, int w = 2560, int h = 1440)
        => PanelHostMessage.Parse(json, w, h);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"reportLayout\"}")]
    [InlineData("{\"type\":5}")]
    [InlineData("[1]")]
    public void NonPanelMessagesFallThroughToWidgets(string json)
        => Assert.Equal(PanelMessageKind.NotPanel, Parse(json).Kind);

    [Fact]
    public void UnknownPanelMessageIsDroppedNotForwarded()
        => Assert.Equal(PanelMessageKind.Ignored, Parse("{\"type\":\"nexus.panel.nope\"}").Kind);

    [Theory]
    [InlineData("{\"type\":\"nexus.panel.raise\",\"value\":true}", true)]
    [InlineData("{\"type\":\"nexus.panel.raise\",\"value\":false}", false)]
    public void RaiseReadsBoolean(string json, bool expected)
    {
        var m = Parse(json);
        Assert.Equal(PanelMessageKind.Raise, m.Kind);
        Assert.Equal(expected, m.RaiseValue);
    }

    [Theory]
    [InlineData("{\"type\":\"nexus.panel.raise\"}")]
    [InlineData("{\"type\":\"nexus.panel.raise\",\"value\":\"true\"}")]
    [InlineData("{\"type\":\"nexus.panel.raise\",\"value\":1}")]
    public void RaiseWithoutBooleanIsIgnored(string json)
        => Assert.Equal(PanelMessageKind.Ignored, Parse(json).Kind);

    [Fact]
    public void FullModeIsWholeWindow()
    {
        var m = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"full\",\"dpr\":1,\"rects\":[]}");
        Assert.Equal(PanelMessageKind.HitTest, m.Kind);
        Assert.Equal(HitMode.Full, m.Plan!.Mode);
    }

    [Fact]
    public void RegionScalesCssPxByDpr()
    {
        var m = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1.5,\"rects\":[{\"x\":100,\"y\":40,\"w\":200,\"h\":80}]}");
        Assert.Equal(HitMode.Rects, m.Plan!.Mode);
        Assert.Equal(new RegionLayout.Rect(150, 60, 300, 120), Assert.Single(m.Plan.Rects));
    }

    [Fact]
    public void RegionRoundsOutwardSoCardEdgesStayHittable()
    {
        // x 10.2*1.25=12.75 -> 12, right (10.2+10)*1.25=25.25 -> 26
        var m = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1.25,\"rects\":[{\"x\":10.2,\"y\":0,\"w\":10,\"h\":10}]}");
        Assert.Equal(new RegionLayout.Rect(12, 0, 14, 13), Assert.Single(m.Plan!.Rects));
    }

    [Fact]
    public void RegionClampsToWindowAndDropsOffscreenRects()
    {
        var m = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[{\"x\":-50,\"y\":-50,\"w\":100,\"h\":100},{\"x\":950,\"y\":500,\"w\":200,\"h\":200},{\"x\":5000,\"y\":5000,\"w\":10,\"h\":10}]}", 1000, 600);
        Assert.Equal(2, m.Plan!.Rects.Count);
        Assert.Equal(new RegionLayout.Rect(0, 0, 50, 50), m.Plan.Rects[0]);
        Assert.Equal(new RegionLayout.Rect(950, 500, 50, 100), m.Plan.Rects[1]);
    }

    [Theory]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":0,\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":-2,\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":99,\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"bogus\",\"dpr\":1,\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"dpr\":1,\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}")]
    [InlineData("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[{\"x\":0,\"y\":0,\"w\":0,\"h\":10},{\"x\":\"a\",\"y\":0,\"w\":5,\"h\":5},{\"x\":0,\"y\":0,\"w\":-5,\"h\":5}]}")]
    public void UnusableReportsFailSafeToEmpty(string json)
    {
        var m = Parse(json);
        Assert.Equal(PanelMessageKind.HitTest, m.Kind);
        Assert.Equal(HitMode.Empty, m.Plan!.Mode);
    }

    [Fact]
    public void ZeroSizedWindowIsEmpty()
    {
        var m = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[{\"x\":0,\"y\":0,\"w\":10,\"h\":10}]}", 0, 0);
        Assert.Equal(HitMode.Empty, m.Plan!.Mode);
    }

    [Fact]
    public void RectCountIsCapped()
    {
        var items = string.Join(",", System.Linq.Enumerable.Range(0, 400).Select(i => $"{{\"x\":{i},\"y\":0,\"w\":1,\"h\":1}}"));
        var m = Parse($"{{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[{items}]}}");
        Assert.Equal(PanelHostMessage.MaxRects, m.Plan!.Rects.Count);
    }

    [Fact]
    public void HugeCoordinatesDoNotOverflow()
    {
        var m = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":16,\"rects\":[{\"x\":1e300,\"y\":0,\"w\":1e300,\"h\":10},{\"x\":0,\"y\":0,\"w\":1e18,\"h\":1e18}]}", 800, 600);
        Assert.Equal(new RegionLayout.Rect(0, 0, 800, 600), Assert.Single(m.Plan!.Rects));
    }

    [Fact]
    public void PlansCompareByContent()
    {
        var a = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[{\"x\":1,\"y\":2,\"w\":3,\"h\":4}]}").Plan!;
        var b = Parse("{\"type\":\"nexus.panel.hitTest\",\"mode\":\"region\",\"dpr\":1,\"rects\":[{\"x\":1,\"y\":2,\"w\":3,\"h\":4}]}").Plan!;
        Assert.True(a.Equals(b));
        Assert.False(a.Equals(HitRegionPlan.Empty));
    }
}
