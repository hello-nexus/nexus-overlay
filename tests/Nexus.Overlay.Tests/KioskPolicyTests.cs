using Xunit;

namespace Nexus.Overlay.Tests;

public class KioskPolicyTests
{
    private static DisplayAssignment A(string backdrop = "", bool reserve = true, bool keepOff = false,
        bool top = true, bool click = false, bool primary = false) => new()
    {
        DisplayId = "d", PanelDeviceId = "p", Backdrop = backdrop, ReserveMonitor = reserve,
        KeepCursorOff = keepOff, AlwaysOnTop = top, ClickThrough = click, IsPrimary = primary,
    };

    [Fact]
    public void SecondaryKeepsTodaysBehavior()
    {
        var t = KioskPolicy.Resolve(A(reserve: true, keepOff: true), monitorIsPrimary: false);
        Assert.True(t.Reserve);
        Assert.True(t.KeepCursorOff);
        Assert.True(t.AllowTaskbarGuard);
        Assert.False(t.IsPrimary);
        Assert.True(t.Topmost);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PrimaryDropsEveryGuardWhicheverSideSaysPrimary(bool assignmentPrimary, bool monitorPrimary)
    {
        var t = KioskPolicy.Resolve(A(reserve: true, keepOff: true, primary: assignmentPrimary), monitorPrimary);
        Assert.True(t.IsPrimary);
        Assert.False(t.Reserve);
        Assert.False(t.KeepCursorOff);
        Assert.False(t.AllowTaskbarGuard);
    }

    [Theory]
    [InlineData("desktop", true, true)]
    [InlineData("desktop", false, false)]
    [InlineData("", true, false)]
    [InlineData("color", true, false)]
    public void ClickThroughNeedsSeeThrough(string backdrop, bool click, bool expected)
        => Assert.Equal(expected, KioskPolicy.Resolve(A(backdrop, click: click), false).ClickThrough);

    [Fact]
    public void OlderServiceWithoutNewFieldsMeansNoClickThroughAndNotPrimary()
    {
        var a = System.Text.Json.JsonSerializer.Deserialize(
            "{\"displayId\":\"d\",\"panelDeviceId\":\"p\",\"backdrop\":\"desktop\"}", ApiJson.Default.DisplayAssignment)!;
        var t = KioskPolicy.Resolve(a, false);
        Assert.False(t.ClickThrough);
        Assert.False(t.IsPrimary);
    }

    [Fact]
    public void NewFieldsRoundTripFromServiceJson()
    {
        var a = System.Text.Json.JsonSerializer.Deserialize(
            "{\"displayId\":\"d\",\"panelDeviceId\":\"p\",\"backdrop\":\"desktop\",\"clickThrough\":true,\"isPrimary\":true,\"alwaysOnTop\":false}",
            ApiJson.Default.DisplayAssignment)!;
        Assert.True(a.ClickThrough);
        Assert.True(a.IsPrimary);
        Assert.False(a.AlwaysOnTop);
    }

    [Fact]
    public void SeeThroughAndPrimaryChangesRespawnOthersDoNot()
    {
        var live = KioskPolicy.Resolve(A("desktop", click: false), false);
        Assert.False(KioskPolicy.NeedsRespawn(live, KioskPolicy.Resolve(A("desktop", click: true), false)));
        Assert.False(KioskPolicy.NeedsRespawn(live, KioskPolicy.Resolve(A("desktop", top: false), false)));
        Assert.False(KioskPolicy.NeedsRespawn(live, KioskPolicy.Resolve(A("desktop", reserve: false), false)));
        Assert.True(KioskPolicy.NeedsRespawn(live, KioskPolicy.Resolve(A(""), false)));
        Assert.True(KioskPolicy.NeedsRespawn(live, KioskPolicy.Resolve(A("desktop"), true)));
    }

    [Fact]
    public void MonitorMapSkipsUnresolvableAndKeepsIndexAndPrimary()
    {
        var map = MonitorMap.Build(
            new[] { (0, @"\\.\DISPLAY1", true), (1, @"\\.\DISPLAY2", false), (2, @"\\.\DISPLAY3", false) },
            n => n.EndsWith("2") ? "" : "id-" + n[^1]);
        Assert.Equal(2, map.Count);
        Assert.Equal((0, "id-1", true), (map[0].Index, map[0].DisplayId, map[0].Primary));
        Assert.Equal((2, "id-3", false), (map[1].Index, map[1].DisplayId, map[1].Primary));
    }

    [Fact]
    public void MonitorMapSignatureChangesWithContentAndSerializesToContract()
    {
        var a = MonitorMap.Build(new[] { (0, "a", true) }, n => "x");
        var b = MonitorMap.Build(new[] { (1, "a", true) }, n => "x");
        Assert.NotEqual(MonitorMap.Signature(a), MonitorMap.Signature(b));
        Assert.Equal(MonitorMap.Signature(a), MonitorMap.Signature(MonitorMap.Build(new[] { (0, "a", true) }, n => "x")));
        var json = System.Text.Json.JsonSerializer.Serialize(new OverlayMonitorsBody { Monitors = a }, ApiJson.Default.OverlayMonitorsBody);
        Assert.Equal("{\"monitors\":[{\"index\":0,\"displayId\":\"x\",\"primary\":true}]}", json);
    }

    private static readonly KioskBounds.Box Mon = new(0, 0, 2560, 1440);

    [Theory]
    [InlineData(0, 0, 2560, 1400)]
    [InlineData(0, 40, 2560, 1440)]
    [InlineData(60, 0, 2560, 1440)]
    public void PrimaryFillsTheWorkArea(int l, int t, int r, int b)
    {
        var work = new KioskBounds.Box(l, t, r, b);
        Assert.Equal(work, KioskBounds.Target(true, Mon, work));
    }

    [Fact]
    public void NonPrimaryCoversTheFullMonitorWhateverTheWorkArea()
        => Assert.Equal(Mon, KioskBounds.Target(false, Mon, new KioskBounds.Box(0, 0, 2560, 1400)));

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(100, 100, 100, 200)]
    [InlineData(-10, 0, 2560, 1400)]
    [InlineData(0, 0, 2600, 1400)]
    public void PrimaryFallsBackToMonitorForUnusableWorkArea(int l, int t, int r, int b)
        => Assert.Equal(Mon, KioskBounds.Target(true, Mon, new KioskBounds.Box(l, t, r, b)));

    [Fact]
    public void PrimaryOnAnOffsetMonitorKeepsItsOrigin()
    {
        var mon = new KioskBounds.Box(-1920, 0, 0, 1080);
        var work = new KioskBounds.Box(-1920, 0, 0, 1040);
        Assert.Equal(work, KioskBounds.Target(true, mon, work));
    }

    [Fact]
    public void FullBoundsKioskHasZeroOrigin()
        => Assert.Equal("&originX=0&originY=0", KioskBounds.OriginQuery(Mon, Mon, 144));

    [Theory]
    [InlineData(96u, "&originX=0&originY=40")]
    [InlineData(144u, "&originX=0&originY=26.67")]
    [InlineData(0u, "&originX=0&originY=40")]
    public void TopTaskbarOffsetIsInCssPx(uint dpi, string expected)
        => Assert.Equal(expected, KioskBounds.OriginQuery(new KioskBounds.Box(0, 40, 2560, 1440), Mon, dpi));

    [Fact]
    public void LeftTaskbarOffsetAndOffsetMonitorOriginUseDifferences()
    {
        var mon = new KioskBounds.Box(-1920, 0, 0, 1080);
        var work = new KioskBounds.Box(-1860, 0, 0, 1080);
        Assert.Equal("&originX=60&originY=0", KioskBounds.OriginQuery(work, mon, 96));
    }
}
