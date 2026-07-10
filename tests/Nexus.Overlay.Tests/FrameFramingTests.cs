using System;
using Nexus.Overlay.Media;
using Xunit;

namespace Nexus.Overlay.Tests;

public class FrameFramingTests
{
    [Fact]
    public void Length_is_little_endian()
    {
        var header = new byte[FrameFraming.HeaderSize];
        FrameFraming.WriteHeader(header, 0x01020304, flags: 0);

        Assert.Equal(new byte[] { 0x04, 0x03, 0x02, 0x01, 0x00 }, header);
    }

    [Fact]
    public void Flags_land_in_the_fifth_byte()
    {
        var header = new byte[FrameFraming.HeaderSize];
        FrameFraming.WriteHeader(header, 16, FrameFraming.FlagIdr);

        Assert.Equal(new byte[] { 0x10, 0x00, 0x00, 0x00, 0x01 }, header);
    }

    [Fact]
    public void Zero_length_control_frame_header()
    {
        var header = new byte[FrameFraming.HeaderSize];
        FrameFraming.WriteHeader(header, 0, FrameFraming.FlagControl);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x02 }, header);
    }

    [Fact]
    public void Destination_smaller_than_header_throws()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            Span<byte> tooSmall = stackalloc byte[FrameFraming.HeaderSize - 1];
            FrameFraming.WriteHeader(tooSmall, 1, 0);
        });
    }
}
