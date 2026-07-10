using Nexus.Overlay.Media;
using Xunit;

namespace Nexus.Overlay.Tests;

public class AnnexBTests
{
    [Fact]
    public void Idr_after_4_byte_start_code()
    {
        var au = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84 };
        Assert.True(AnnexB.ContainsIdr(au));
    }

    [Fact]
    public void Idr_after_3_byte_start_code()
    {
        var au = new byte[] { 0x00, 0x00, 0x01, 0x65, 0x88, 0x84 };
        Assert.True(AnnexB.ContainsIdr(au));
    }

    [Fact]
    public void Idr_found_behind_aud_sps_pps()
    {
        var au = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x09, 0x10,             // AUD (type 9)
            0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F, // SPS (type 7)
            0x00, 0x00, 0x00, 0x01, 0x68, 0xCE, 0x3C, 0x80, // PPS (type 8)
            0x00, 0x00, 0x01, 0x65, 0x88, 0x84,             // IDR (type 5)
        };
        Assert.True(AnnexB.ContainsIdr(au));
    }

    [Fact]
    public void Non_idr_slice_only_is_false()
    {
        var au = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41, 0x9A, 0x22 }; // P slice (type 1)
        Assert.False(AnnexB.ContainsIdr(au));
    }

    [Fact]
    public void Empty_input_is_false()
    {
        Assert.False(AnnexB.ContainsIdr(default));
        Assert.False(AnnexB.ContainsIdr(new byte[0]));
    }

    [Fact]
    public void Truncated_start_code_at_end_is_false()
    {
        Assert.False(AnnexB.ContainsIdr(new byte[] { 0x00, 0x00, 0x01 }));
        Assert.False(AnnexB.ContainsIdr(new byte[] { 0x00, 0x00, 0x00, 0x01 }));
        Assert.False(AnnexB.ContainsIdr(new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void Type_5_byte_inside_payload_without_start_code_does_not_count()
    {
        var au = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x41, 0x65, 0x25, 0x65 };
        Assert.False(AnnexB.ContainsIdr(au));
    }
}
