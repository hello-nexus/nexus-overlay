using Nexus.Overlay.Media;
using Xunit;

namespace Nexus.Overlay.Tests;

public class EncoderSelectTests
{
    private static readonly string?[] DualGpuNames =
    {
        "NVIDIA H.264 Encoder MFT",
        "AMDh264Encoder",
    };

    [Fact]
    public void No_vendor_keeps_enumeration_order()
    {
        Assert.Equal(new[] { 0, 1 }, EncoderSelect.ActivationOrder(DualGpuNames, 0));
    }

    [Fact]
    public void Unmapped_vendor_keeps_enumeration_order()
    {
        Assert.Equal(new[] { 0, 1 }, EncoderSelect.ActivationOrder(DualGpuNames, 0x1414));
    }

    [Fact]
    public void Amd_vendor_moves_amd_mft_first()
    {
        Assert.Equal(new[] { 1, 0 }, EncoderSelect.ActivationOrder(DualGpuNames, 0x1002));
    }

    [Fact]
    public void Nvidia_vendor_keeps_nvidia_mft_first()
    {
        Assert.Equal(new[] { 0, 1 }, EncoderSelect.ActivationOrder(DualGpuNames, 0x10DE));
    }

    [Fact]
    public void Intel_marker_matches_quick_sync_name()
    {
        var names = new string?[] { "AMDh264Encoder", "IntelR Quick Sync Video H.264 Encoder MFT" };
        Assert.Equal(new[] { 1, 0 }, EncoderSelect.ActivationOrder(names, 0x8086));
    }

    [Fact]
    public void Null_names_go_after_matches_and_keep_relative_order()
    {
        var names = new string?[] { null, "AMDh264Encoder", null };
        Assert.Equal(new[] { 1, 0, 2 }, EncoderSelect.ActivationOrder(names, 0x1002));
    }

    [Fact]
    public void Matched_group_keeps_enumeration_order()
    {
        var names = new string?[] { "NVIDIA H.264 Encoder MFT", "AMDh264Encoder", "AMDh264Encoder" };
        Assert.Equal(new[] { 1, 2, 0 }, EncoderSelect.ActivationOrder(names, 0x1002));
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        var names = new string?[] { "NVIDIA H.264 Encoder MFT", "amdH264encoder" };
        Assert.Equal(new[] { 1, 0 }, EncoderSelect.ActivationOrder(names, 0x1002));
    }

    [Fact]
    public void Empty_list_returns_empty_order()
    {
        Assert.Empty(EncoderSelect.ActivationOrder(System.Array.Empty<string?>(), 0x1002));
    }
}
