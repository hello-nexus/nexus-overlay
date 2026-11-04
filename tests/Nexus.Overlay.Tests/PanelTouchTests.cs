using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public class PanelTouchTests
{
    [Theory]
    [InlineData("1 down 0.5 0.25", 1u, "Down", 0.5, 0.25)]
    [InlineData("2 move 0 1", 2u, "Move", 0, 1)]
    [InlineData("3 up 1.5 -0.5", 3u, "Up", 1, 0)]
    public void Lines_parse_and_clamp_to_the_page(string line, uint id, string phase, double x, double y)
    {
        Assert.True(PanelTouchContact.TryParse(line, out var contact));
        Assert.Equal(new PanelTouchContact(id, Enum.Parse<PanelTouchPhase>(phase), x, y), contact);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1 tap 0.5 0.5")]
    [InlineData("1 down 0,5 0.5")]
    [InlineData("-1 down 0.5 0.5")]
    [InlineData("1 down 0.5")]
    public void Keepalives_and_malformed_lines_are_skipped(string line)
    {
        Assert.False(PanelTouchContact.TryParse(line, out _));
    }

    [Fact]
    public void One_contact_starts_moves_and_ends_in_css_pixels()
    {
        var state = new CdpTouchState();

        Assert.Equal("{\"type\":\"touchStart\",\"touchPoints\":[{\"x\":640,\"y\":110,\"id\":1}]}", state.Apply(new(1, PanelTouchPhase.Down, 0.5, 0.25), 1280, 440));
        Assert.Equal("{\"type\":\"touchMove\",\"touchPoints\":[{\"x\":128,\"y\":44,\"id\":1}]}", state.Apply(new(1, PanelTouchPhase.Move, 0.1, 0.1), 1280, 440));
        Assert.Equal("{\"type\":\"touchEnd\",\"touchPoints\":[]}", state.Apply(new(1, PanelTouchPhase.Up, 0.1, 0.1), 1280, 440));
        Assert.Null(state.ReleaseAll());
    }

    [Fact]
    public void Every_event_carries_all_points_and_a_partial_release_is_a_move()
    {
        var state = new CdpTouchState();
        state.Apply(new(1, PanelTouchPhase.Down, 0, 0), 100, 100);

        Assert.Equal("{\"type\":\"touchStart\",\"touchPoints\":[{\"x\":0,\"y\":0,\"id\":1},{\"x\":50,\"y\":50,\"id\":2}]}", state.Apply(new(2, PanelTouchPhase.Down, 0.5, 0.5), 100, 100));
        Assert.Equal("{\"type\":\"touchMove\",\"touchPoints\":[{\"x\":50,\"y\":50,\"id\":2}]}", state.Apply(new(1, PanelTouchPhase.Up, 0, 0), 100, 100));
        Assert.Equal("{\"type\":\"touchCancel\",\"touchPoints\":[]}", state.ReleaseAll());
    }

    [Fact]
    public void Contacts_for_points_never_down_change_nothing()
    {
        var state = new CdpTouchState();

        Assert.Null(state.Apply(new(4, PanelTouchPhase.Move, 0.5, 0.5), 100, 100));
        Assert.Null(state.Apply(new(4, PanelTouchPhase.Up, 0.5, 0.5), 100, 100));
    }
}
