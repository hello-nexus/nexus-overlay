using System.Text.Json;
using Nexus.Overlay;
using Xunit;

public class OverlayStateTests
{
    [Fact]
    public void Parses_the_service_document()
    {
        const string json = """
            {"autoLaunch":true,"reserveMonitor":false,"y70Backdrop":"desktop","overlayEnabled":true,"alwaysOnTop":true,"monitor":1,"pinned":2,
             "assignments":[{"displayId":"d1","panelDeviceId":"p1","reserveMonitor":true,"backdrop":"wallpaper"}],
             "streams":[{"sessionId":"s1","panelDeviceId":"p2","cssWidth":800,"cssHeight":480,"dpr":1.5,"fps":30,"bitrateKbps":4000,"codec":"h264"}]}
            """;
        var state = JsonSerializer.Deserialize(json, ApiJson.Default.OverlayState)!;
        Assert.True(state.AutoLaunch);
        Assert.False(state.ReserveMonitor);
        Assert.Equal("desktop", state.Y70Backdrop);
        Assert.Equal(2, state.Pinned);
        Assert.Equal(1, state.Monitor);
        Assert.Single(state.Assignments);
        Assert.Equal("wallpaper", state.Assignments[0].Backdrop);
        Assert.Single(state.Streams);
        Assert.Equal(1.5, state.Streams[0].Dpr);
    }

    [Fact]
    public void Missing_fields_take_the_safe_defaults()
    {
        var state = JsonSerializer.Deserialize("{}", ApiJson.Default.OverlayState)!;
        Assert.False(state.AutoLaunch);
        Assert.True(state.ReserveMonitor);
        Assert.Equal("", state.Y70Backdrop);
        Assert.Equal(-1, state.Monitor);
        Assert.Empty(state.Assignments);
        Assert.Empty(state.Streams);
    }
}
