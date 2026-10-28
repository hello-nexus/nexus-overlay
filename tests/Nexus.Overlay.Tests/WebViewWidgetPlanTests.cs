using Nexus.Overlay;
using Xunit;

namespace Nexus.Overlay.Tests;

public class WebViewWidgetPlanTests
{
    private static string View(string id, string url = "https://example.com/", double x = 10, double y = 20,
        double w = 100, double h = 50, bool interactive = false, bool visible = true)
        => $"{{\"id\":\"{id}\",\"url\":\"{url}\",\"rect\":{{\"x\":{x},\"y\":{y},\"w\":{w},\"h\":{h}}},"
           + $"\"interactive\":{(interactive ? "true" : "false")},\"visible\":{(visible ? "true" : "false")}}}";

    private static WidgetMessage Sync(long seq, params string[] views)
        => WebViewWidgetProtocol.Parse(
            $"{{\"type\":\"nexus.webview.sync\",\"seq\":{seq},\"viewport\":{{\"w\":1000,\"h\":500}},\"views\":[{string.Join(",", views)}]}}");

    [Fact]
    public void Parse_Hello()
        => Assert.Equal(WidgetMessageKind.Hello, WebViewWidgetProtocol.Parse("{\"type\":\"nexus.webview.hello\",\"version\":1}").Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"type\":5}")]
    [InlineData("{\"type\":\"other\"}")]
    [InlineData("{\"type\":\"nexus.webview.sync\"}")]
    [InlineData("{\"type\":\"nexus.webview.command\",\"id\":\"a\",\"command\":\"forward\"}")]
    public void Parse_Malformed_IsUnknown(string? json)
        => Assert.Equal(WidgetMessageKind.Unknown, WebViewWidgetProtocol.Parse(json).Kind);

    [Fact]
    public void Parse_Command()
    {
        var m = WebViewWidgetProtocol.Parse("{\"type\":\"nexus.webview.command\",\"id\":\"w1\",\"command\":\"back\"}");
        Assert.Equal(WidgetMessageKind.Command, m.Kind);
        Assert.Equal("w1", m.CommandId);
        Assert.Equal("back", m.Command);
    }

    [Fact]
    public void Parse_Sync_ReadsViews()
    {
        var m = Sync(7, View("a", interactive: true, visible: false));
        Assert.Equal(WidgetMessageKind.Sync, m.Kind);
        Assert.Equal(7, m.Seq);
        Assert.Equal(1000, m.ViewportW);
        var v = Assert.Single(m.Views);
        Assert.Equal("a", v.Id);
        Assert.Equal(new WidgetRect(10, 20, 100, 50), v.Rect);
        Assert.True(v.Interactive);
        Assert.False(v.Visible);
    }

    [Fact]
    public void Parse_Sync_CapsAtMaxViews_AndReportsOverflow()
    {
        var m = Sync(1, View("a"), View("b"), View("c"), View("d"), View("e"), View("f"));
        Assert.Equal(WebViewWidgetProtocol.MaxViews, m.Views.Count);
        Assert.Equal(new[] { "e", "f" }, m.OverflowIds);
    }

    [Fact]
    public void Parse_Sync_DuplicateIdKeepsFirst()
    {
        var m = Sync(1, View("a", "https://one.test/"), View("a", "https://two.test/"));
        Assert.Equal("https://one.test/", Assert.Single(m.Views).Url);
    }

    [Theory]
    [InlineData("https://example.com/path?q=1", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedUrl(string? url, bool expected)
        => Assert.Equal(expected, WebViewWidgetProtocol.IsAllowedUrl(url));

    [Fact]
    public void Parse_Sync_InvalidUrlIsReportedNotListed()
    {
        var m = Sync(1, View("bad", "javascript:alert(1)"), View("ok"));
        Assert.Equal("ok", Assert.Single(m.Views).Id);
        Assert.Equal(new[] { "bad" }, m.InvalidIds);
    }

    [Theory]
    [InlineData("http://localhost:9400/panel", "http://localhost:9400/panel?token=x&displayHz=60", true)]
    [InlineData("http://LOCALHOST:9400/", "http://localhost:9400/panel", true)]
    [InlineData("http://localhost:9401/", "http://localhost:9400/panel", false)]
    [InlineData("https://localhost:9400/", "http://localhost:9400/panel", false)]
    [InlineData("https://evil.example/", "http://localhost:9400/panel", false)]
    [InlineData("", "http://localhost:9400/panel", false)]
    [InlineData(null, "http://localhost:9400/panel", false)]
    public void IsPanelOrigin(string? source, string panelUrl, bool expected)
        => Assert.Equal(expected, WebViewWidgetProtocol.IsPanelOrigin(source, panelUrl));

    [Fact]
    public void MapRect_ScalesPerAxis()
    {
        var px = WebViewWidgetProtocol.MapRect(new WidgetRect(100, 50, 200, 100), 1000, 500, 2000, 1500);
        Assert.Equal(new WidgetPixelRect(200, 150, 400, 300), px);
    }

    [Fact]
    public void MapRect_ClampsToBounds()
    {
        var px = WebViewWidgetProtocol.MapRect(new WidgetRect(-50, 450, 2000, 200), 1000, 500, 1000, 500);
        Assert.Equal(new WidgetPixelRect(0, 450, 1000, 50), px);
    }

    [Theory]
    [InlineData(0, 500, 100, 100)]
    [InlineData(1000, 0, 100, 100)]
    [InlineData(1000, 500, 0, 100)]
    [InlineData(1000, 500, 100, 0)]
    public void MapRect_Unusable_IsEmpty(double vw, double vh, int bw, int bh)
        => Assert.True(WebViewWidgetProtocol.MapRect(new WidgetRect(0, 0, 10, 10), vw, vh, bw, bh).IsEmpty);

    [Fact]
    public void MapRect_NonFinite_IsEmpty()
        => Assert.True(WebViewWidgetProtocol.MapRect(new WidgetRect(double.NaN, 0, 10, 10), 1000, 500, 1000, 500).IsEmpty);

    [Fact]
    public void Messages_AreJson()
    {
        Assert.Equal("{\"type\":\"nexus.webview.caps\",\"version\":1,\"supported\":true,\"maxViews\":4,\"interactive\":true,\"contextMenu\":true,\"longPress\":true}",
            WebViewWidgetProtocol.BuildCaps(true));
        Assert.Equal("{\"type\":\"nexus.webview.state\",\"id\":\"a\",\"state\":\"error\",\"code\":\"12\",\"canGoBack\":false}",
            WebViewWidgetProtocol.BuildState("a", "error", "12", false));
        Assert.Equal("{\"type\":\"nexus.webview.state\",\"id\":\"a\",\"state\":\"loaded\",\"canGoBack\":true}",
            WebViewWidgetProtocol.BuildState("a", "loaded", null, true));
        Assert.Equal("{\"type\":\"nexus.webview.contextmenu\",\"id\":\"a\",\"x\":10.5,\"y\":20}",
            WebViewWidgetProtocol.BuildContextMenu("a", 10.46, 20));
    }

    [Fact]
    public void UnmapPoint_InvertsTheViewportScale()
    {
        Assert.Equal((100.0, 50.0), WebViewWidgetProtocol.UnmapPoint(150, 75, 1280, 720, 1920, 1080));
        Assert.Null(WebViewWidgetProtocol.UnmapPoint(1, 1, 0, 720, 1920, 1080));
        Assert.Null(WebViewWidgetProtocol.UnmapPoint(1, 1, 1280, 720, 0, 1080));
    }

    [Fact]
    public void Registry_FirstSyncCreates()
    {
        var reg = new WidgetRegistry();
        var actions = reg.Apply(Sync(1, View("a")), 0);
        Assert.Equal(WidgetActionKind.Create, Assert.Single(actions).Kind);
        Assert.Equal(1, reg.LiveCount);
    }

    [Fact]
    public void Registry_RepeatedSyncIsNoOp()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        Assert.Empty(reg.Apply(Sync(2, View("a")), 1000));
    }

    [Fact]
    public void Registry_RectAndInteractiveChangeLayoutOnly()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        var actions = reg.Apply(Sync(2, View("a", x: 500, w: 300, interactive: true, visible: false)), 10);
        var a = Assert.Single(actions);
        Assert.Equal(WidgetActionKind.Layout, a.Kind);
        Assert.True(a.Spec!.Interactive);
    }

    [Fact]
    public void Registry_TileToImmersiveToTile_NeverRecreatesOrNavigates()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        var immersive = reg.Apply(Sync(2, View("a", x: 0, y: 40, w: 1000, h: 460, interactive: true)), 10);
        var tile = reg.Apply(Sync(3, View("a")), 20);
        Assert.All(immersive.Concat(tile), act => Assert.Equal(WidgetActionKind.Layout, act.Kind));
    }

    [Fact]
    public void Registry_NavigatesOnlyWhenConfiguredUrlChanges()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a", "https://one.test/")), 0);
        Assert.Equal("https://one.test/", reg.ConfiguredUrl("a"));
        var actions = reg.Apply(Sync(2, View("a", "https://two.test/")), 10);
        Assert.Equal(WidgetActionKind.Navigate, Assert.Single(actions).Kind);
        Assert.Empty(reg.Apply(Sync(3, View("a", "https://two.test/")), 20));
        Assert.Equal(WidgetActionKind.Navigate, Assert.Single(reg.Apply(Sync(4, View("a", "https://one.test/")), 30)).Kind);
    }

    [Fact]
    public void Registry_DestroysViewsNotListed()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a"), View("b")), 0);
        var actions = reg.Apply(Sync(2, View("b")), 10);
        var d = Assert.Single(actions);
        Assert.Equal(WidgetActionKind.Destroy, d.Kind);
        Assert.Equal("a", d.Id);
        Assert.Equal(new[] { "b" }, reg.LiveIds);
    }

    [Fact]
    public void Registry_EmptySyncDestroysAll()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a"), View("b")), 0);
        Assert.Equal(2, reg.Apply(Sync(2), 10).Count(x => x.Kind == WidgetActionKind.Destroy));
        Assert.Equal(0, reg.LiveCount);
    }

    [Fact]
    public void Registry_IgnoresStaleSeq()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(5, View("a")), 0);
        Assert.Empty(reg.Apply(Sync(4), 10));
        Assert.Equal(1, reg.LiveCount);
    }

    [Fact]
    public void Registry_ReportsOverflowAndInvalidOnce()
    {
        var reg = new WidgetRegistry();
        var first = reg.Apply(Sync(1, View("bad", "file:///x"), View("a"), View("b"), View("c"), View("d"), View("e")), 0);
        Assert.Equal(1, first.Count(x => x.Kind == WidgetActionKind.ReportError && x.Code == "limit"));
        Assert.Equal(1, first.Count(x => x.Kind == WidgetActionKind.ReportError && x.Code == "url"));
        var again = reg.Apply(Sync(2, View("bad", "file:///x"), View("a"), View("b"), View("c"), View("d"), View("e")), 100);
        Assert.Empty(again);
    }

    [Fact]
    public void Registry_Watchdog()
    {
        var reg = new WidgetRegistry();
        Assert.False(reg.WatchdogExpired(100_000));
        reg.Apply(Sync(1, View("a")), 1000);
        Assert.False(reg.WatchdogExpired(1000 + WidgetRegistry.WatchdogMs - 1));
        Assert.True(reg.WatchdogExpired(1000 + WidgetRegistry.WatchdogMs));
        reg.Apply(Sync(2, View("a")), 3500);
        Assert.False(reg.WatchdogExpired(5000));
    }

    [Fact]
    public void Registry_ClearForgetsViewsAndRecreatesOnNextSync()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        Assert.Equal(new[] { "a" }, reg.Clear());
        Assert.False(reg.WatchdogExpired(100_000));
        Assert.Equal(WidgetActionKind.Create, Assert.Single(reg.Apply(Sync(1, View("a")), 10)).Kind);
    }

    [Fact]
    public void Registry_CrashRecreatesOnlyAfterBackoff()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a"), View("b")), 0);
        reg.MarkCrashed("a", 1000);
        Assert.False(reg.IsLive("a"));
        Assert.True(reg.IsLive("b"));
        Assert.Empty(reg.Apply(Sync(2, View("a"), View("b")), 1000 + WidgetRegistry.CrashBackoffMs - 1));
        var actions = reg.Apply(Sync(3, View("a"), View("b")), 1000 + WidgetRegistry.CrashBackoffMs);
        var c = Assert.Single(actions);
        Assert.Equal(WidgetActionKind.Create, c.Kind);
        Assert.Equal("a", c.Id);
    }

    [Fact]
    public void Registry_CrashBackoffSurvivesClear_ButNotUnlisting()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        reg.MarkCrashed("a", 1000);
        reg.Clear();
        Assert.Empty(reg.Apply(Sync(1, View("a")), 2000));
        reg.Apply(Sync(2), 2500);
        Assert.Equal(WidgetActionKind.Create, Assert.Single(reg.Apply(Sync(3, View("a")), 2600)).Kind);
    }

    [Theory]
    [InlineData("\"radius\":12", 12)]
    [InlineData("\"radius\":-5", 0)]
    [InlineData("\"radius\":\"x\"", 0)]
    [InlineData("\"radius\":99999", 1000)]
    [InlineData("\"other\":1", 0)]
    public void Parse_Sync_RadiusIsDefensive(string field, double expected)
    {
        var view = View("a").TrimEnd('}') + "," + field + "}";
        Assert.Equal(expected, Assert.Single(Sync(1, view).Views).Radius);
    }

    [Fact]
    public void MapRadius_ScalesPerAxis()
    {
        Assert.Equal((20, 30), WebViewWidgetProtocol.MapRadius(10, 1000, 500, 2000, 1500));
        Assert.Equal((0, 0), WebViewWidgetProtocol.MapRadius(0, 1000, 500, 2000, 1500));
        Assert.Equal((0, 0), WebViewWidgetProtocol.MapRadius(10, 0, 500, 2000, 1500));
    }

    [Fact]
    public void Registry_RadiusChangeIsLayoutOnly()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        var radiusView = View("a").TrimEnd('}') + ",\"radius\":8}";
        Assert.Equal(WidgetActionKind.Layout, Assert.Single(reg.Apply(Sync(2, radiusView), 10)).Kind);
    }

    [Theory]
    [InlineData("\"zoom\":1.5", 1.5)]
    [InlineData("\"zoom\":0.01", 0.25)]
    [InlineData("\"zoom\":50", 3)]
    [InlineData("\"zoom\":0", 1)]
    [InlineData("\"zoom\":-2", 1)]
    [InlineData("\"zoom\":\"big\"", 1)]
    [InlineData("\"other\":1", 1)]
    public void Parse_Sync_ZoomIsDefensive(string field, double expected)
    {
        var view = View("a").TrimEnd('}') + "," + field + "}";
        Assert.Equal(expected, Assert.Single(Sync(1, view).Views).Zoom);
    }

    [Fact]
    public void Registry_ZoomChangeIsLayoutOnly()
    {
        var reg = new WidgetRegistry();
        reg.Apply(Sync(1, View("a")), 0);
        var zoomView = View("a").TrimEnd('}') + ",\"zoom\":2}";
        var a = Assert.Single(reg.Apply(Sync(2, zoomView), 10));
        Assert.Equal(WidgetActionKind.Layout, a.Kind);
        Assert.Equal(2, a.Spec!.Zoom);
    }

    [Fact]
    public void NavState_DownloadFailureSwallowedOnce()
    {
        var n = new WidgetNavState();
        n.OnNavigationStarting();
        n.OnDownloadCancelled();
        Assert.True(n.TryConsumeDownloadFailure());
        Assert.False(n.TryConsumeDownloadFailure());
    }

    [Fact]
    public void NavState_NextNavigationStartClearsFlag()
    {
        var n = new WidgetNavState();
        n.OnDownloadCancelled();
        n.OnNavigationStarting();
        Assert.False(n.TryConsumeDownloadFailure());
    }

    [Fact]
    public void NavState_KeepsSettledStateAndCode_IgnoresLoading()
    {
        var n = new WidgetNavState();
        Assert.Null(n.SettledState);
        n.OnState("loading", null);
        Assert.Null(n.SettledState);
        n.OnState("error", "12");
        n.OnState("loading", null);
        Assert.Equal("error", n.SettledState);
        Assert.Equal("12", n.SettledCode);
    }

    [Theory]
    [InlineData("{\"nexusEditableFocus\":true}", true)]
    [InlineData("{\"nexusEditableFocus\":false}", false)]
    public void ParseEditFocus_Accepts(string json, bool expected)
        => Assert.Equal(expected, WebViewWidgetProtocol.ParseEditFocus(json));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("\"nexusEditableFocus\"")]
    [InlineData("{}")]
    [InlineData("{\"nexusEditableFocus\":1}")]
    [InlineData("{\"nexusEditableFocus\":\"true\"}")]
    [InlineData("{\"nexusEditableFocus\":true,\"x\":1}")]
    [InlineData("{\"x\":1,\"nexusEditableFocus\":true}")]
    [InlineData("{\"nexusEditableFocus\":true,\"nexusEditableFocus\":false}")]
    [InlineData("{\"type\":\"nexus.webview.sync\"}")]
    [InlineData("not json")]
    public void ParseEditFocus_RejectsEverythingElse(string? json)
        => Assert.Null(WebViewWidgetProtocol.ParseEditFocus(json));

    [Fact]
    public void LongPressGate_DropsRepeatsWithinTheGap()
    {
        var gate = new LongPressGate();
        Assert.True(gate.Accept(true, true, 0));
        Assert.False(gate.Accept(true, true, LongPressGate.MinGapMs - 1));
        Assert.True(gate.Accept(true, true, LongPressGate.MinGapMs));
        Assert.False(gate.Accept(true, true, LongPressGate.MinGapMs + 1));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LongPressGate_RefusesAViewThatIsNotLiveAndInteractive_WithoutStartingTheGap(bool live, bool interactive)
    {
        var gate = new LongPressGate();
        Assert.False(gate.Accept(live, interactive, 0));
        Assert.True(gate.Accept(true, true, 1));
    }

    [Fact]
    public void IsBackSwipe_AcceptsOnlyTheExactMessage()
    {
        Assert.True(WebViewWidgetProtocol.IsBackSwipe("{\"nexusBack\":true}"));
        Assert.False(WebViewWidgetProtocol.IsBackSwipe("{\"nexusBack\":false}"));
        Assert.False(WebViewWidgetProtocol.IsBackSwipe("{\"nexusBack\":true,\"x\":1}"));
        Assert.False(WebViewWidgetProtocol.IsBackSwipe("{\"nexusLongPress\":{\"x\":0.5,\"y\":0.5}}"));
        Assert.False(WebViewWidgetProtocol.IsBackSwipe("true"));
        Assert.False(WebViewWidgetProtocol.IsBackSwipe(null));
    }

    [Fact]
    public void LongPressGate_HonorsAGivenGap()
    {
        var gate = new LongPressGate(300);
        Assert.True(gate.Accept(true, true, 0));
        Assert.False(gate.Accept(true, true, 299));
        Assert.True(gate.Accept(true, true, 300));
    }

    [Fact]
    public void ParseLongPress_AcceptsFractions()
        => Assert.Equal((0.25, 1.0), WebViewWidgetProtocol.ParseLongPress("{\"nexusLongPress\":{\"x\":0.25,\"y\":1}}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"nexusLongPress\":true}")]
    [InlineData("{\"nexusLongPress\":{\"x\":0.5}}")]
    [InlineData("{\"nexusLongPress\":{\"x\":1.5,\"y\":0.5}}")]
    [InlineData("{\"nexusLongPress\":{\"x\":-0.1,\"y\":0.5}}")]
    [InlineData("{\"nexusLongPress\":{\"x\":\"0.5\",\"y\":0.5}}")]
    [InlineData("{\"nexusLongPress\":{\"x\":0.5,\"y\":0.5},\"extra\":1}")]
    [InlineData("{\"nexusEditableFocus\":true}")]
    [InlineData("not json")]
    public void ParseLongPress_RejectsEverythingElse(string? json)
        => Assert.Null(WebViewWidgetProtocol.ParseLongPress(json));

    [Fact]
    public void ParseEditFocus_RejectsALongPress()
        => Assert.Null(WebViewWidgetProtocol.ParseEditFocus("{\"nexusLongPress\":{\"x\":0.5,\"y\":0.5}}"));

    private static WidgetEditFocus LiveFocus()
    {
        var f = new WidgetEditFocus();
        f.SetLive(true);
        return f;
    }

    [Fact]
    public void EditFocus_RequiresPointerDownOnView()
    {
        var f = LiveFocus();
        f.OnReport(true, 1_000);
        Assert.False(f.Focused);
        f.OnPointerDown(2_000);
        f.OnReport(true, 2_100);
        Assert.True(f.Focused);
    }

    [Fact]
    public void EditFocus_PointerDownExpires()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, WidgetEditFocus.PointerWindowMs + 1);
        Assert.False(f.Focused);
    }

    [Fact]
    public void EditFocus_OneActivationPerPointerDown()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, 10);
        f.OnReport(false, 20);
        Assert.True(f.ExpireBlur(20 + WidgetEditFocus.BlurGraceMs));
        f.OnReport(true, 1_000);
        Assert.False(f.Focused);
        f.OnPointerDown(1_100);
        f.OnReport(true, 1_200);
        Assert.True(f.Focused);
    }

    [Fact]
    public void EditFocus_BlurThenRefocusWithinGraceKeepsFocus()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, 10);
        f.OnReport(false, 100);
        Assert.True(f.Focused);
        Assert.Equal(100 + WidgetEditFocus.BlurGraceMs, f.BlurDeadline);
        f.OnReport(true, 200);
        Assert.Null(f.BlurDeadline);
        Assert.False(f.ExpireBlur(10_000));
        Assert.True(f.Focused);
    }

    [Fact]
    public void EditFocus_BlurExpiresOnlyAtDeadline()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, 10);
        f.OnReport(false, 100);
        f.OnReport(false, 300);
        Assert.False(f.ExpireBlur(100 + WidgetEditFocus.BlurGraceMs - 1));
        Assert.True(f.ExpireBlur(100 + WidgetEditFocus.BlurGraceMs));
        Assert.False(f.Focused);
    }

    [Fact]
    public void EditFocus_FalseWithoutFocusArmsNoBlur()
    {
        var f = LiveFocus();
        f.OnReport(false, 10);
        Assert.Null(f.BlurDeadline);
    }

    [Fact]
    public void EditFocus_DisarmDropsPendingPointerDown()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.Disarm();
        f.OnReport(true, 10);
        Assert.False(f.Focused);
    }

    [Fact]
    public void EditFocus_NavigationDropsPendingPointerDown()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnNavigationStarting();
        f.OnReport(true, 10);
        Assert.False(f.Focused);
    }

    [Fact]
    public void EditFocus_StaysFocusedAcrossRepeatedTrue()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, 10);
        f.OnReport(true, 100_000);
        Assert.True(f.Focused);
    }

    [Fact]
    public void EditFocus_NotLive_DropsReportsAndPointerDowns()
    {
        var f = new WidgetEditFocus();
        f.OnPointerDown(0);
        f.SetLive(true);
        f.OnReport(true, 10);
        Assert.False(f.Focused);
    }

    [Fact]
    public void EditFocus_LeavingLiveClearsFocusAndPointerDown()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, 10);
        f.OnPointerDown(20);
        f.SetLive(false);
        Assert.False(f.Focused);
        f.SetLive(true);
        f.OnReport(true, 30);
        Assert.False(f.Focused);
    }

    [Fact]
    public void EditFocus_NavigationClears()
    {
        var f = LiveFocus();
        f.OnPointerDown(0);
        f.OnReport(true, 10);
        f.OnNavigationStarting();
        Assert.False(f.Focused);
    }

    [Fact]
    public void CursorRestoreThrottle_FirstAcquireAlwaysPasses()
    {
        var t = new CursorRestoreThrottle(250);
        Assert.True(t.TryAcquire(0));
    }

    [Theory]
    [InlineData(249, false)]
    [InlineData(250, true)]
    [InlineData(1000, true)]
    public void CursorRestoreThrottle_HonorsInterval(long later, bool expected)
    {
        var t = new CursorRestoreThrottle(250);
        t.TryAcquire(0);
        Assert.Equal(expected, t.TryAcquire(later));
    }

    [Fact]
    public void CursorRestoreThrottle_RejectedCallDoesNotExtendInterval()
    {
        var t = new CursorRestoreThrottle(250);
        t.TryAcquire(100);
        Assert.False(t.TryAcquire(300));
        Assert.True(t.TryAcquire(350));
    }

    [Theory]
    [InlineData(-1, 1000, false)]
    [InlineData(1000, 1000, true)]
    [InlineData(1000, 1100, true)]
    [InlineData(1000, 1101, false)]
    public void RealMouseSignal_IsFresh(long flaggedAt, long now, bool expected)
        => Assert.Equal(expected, RealMouseSignal.IsFresh(flaggedAt, now));

    [Theory]
    [InlineData(-3, true)]
    [InlineData(-1, true)]
    [InlineData(0, null)]
    [InlineData(1, false)]
    [InlineData(4, false)]
    public void CursorCountClamp_NextCall_MovesTowardZero(int count, bool? expected)
        => Assert.Equal(expected, CursorCountClamp.NextCall(count));
}
