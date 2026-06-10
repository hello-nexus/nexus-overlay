using Xunit;

namespace Nexus.Overlay.Tests;

/// <summary>
/// Stable-id extraction vectors. Must produce ids byte-identical to
/// nexus-service's WindowsDisplayIdentity (the assignment key) — these
/// vectors mirror that algorithm's behavior.
/// </summary>
public class DisplayIdentityTests
{
    [Fact]
    public void Extracts_edid_segment_between_first_and_last_hash()
    {
        var id = DisplayIdentity.ExtractStableId(
            @"\\?\DISPLAY#DEL41B7#5&abc&0&UID12345#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
            @"\\.\DISPLAY1");
        Assert.Equal("DEL41B7-5-abc-0-UID12345", id);
    }

    [Fact]
    public void Y70_controller_id_round_trips()
    {
        var id = DisplayIdentity.ExtractStableId(
            @"\\?\DISPLAY#RTK0004#5&def&0&UID67890#{guid}",
            @"\\.\DISPLAY2");
        Assert.Equal("RTK0004-5-def-0-UID67890", id);
    }

    [Fact]
    public void Empty_device_id_falls_back_to_adapter_tail()
    {
        Assert.Equal("display3", DisplayIdentity.ExtractStableId("", @"\\.\DISPLAY3"));
    }

    [Fact]
    public void Sanitizes_url_hostile_characters()
    {
        var id = DisplayIdentity.ExtractStableId(@"\\?\DISPLAY#AB C/1#x#{g}", @"\\.\DISPLAY1");
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain(' ', id);
        Assert.DoesNotContain('#', id);
    }
}
