using System;
using System.Collections.Generic;

namespace Nexus.Overlay.Media;

/// <summary>
/// Activation order for the hardware H.264 encoder MFTs MFTEnumEx returns.
/// A vendor's hardware MFT binds only to a D3D device on that vendor's
/// adapter: activation succeeds regardless, then SET_D3D_MANAGER fails on a
/// foreign device. When the stream device is pinned to an adapter, the
/// matching vendor's MFTs must be tried first; MFTEnumEx ordering does not
/// account for the device on a multi-GPU box.
/// </summary>
internal static class EncoderSelect
{
    /// <summary>
    /// Friendly-name marker per PCI vendor id; null = no constraint. Names
    /// observed in the field: "NVIDIA H.264 Encoder MFT", "AMDh264Encoder",
    /// "IntelR Quick Sync Video H.264 Encoder MFT".
    /// </summary>
    public static string? VendorMarker(uint pciVendorId) => pciVendorId switch
    {
        0x1002 => "AMD",
        0x10DE => "NVIDIA",
        0x8086 => "Intel",
        _ => null,
    };

    /// <summary>
    /// Indices of <paramref name="friendlyNames"/> in activation order:
    /// names carrying the vendor's marker first, enumeration order preserved
    /// within each group. Vendor id 0 or an unmapped vendor keeps the
    /// original order.
    /// </summary>
    public static int[] ActivationOrder(IReadOnlyList<string?> friendlyNames, uint pciVendorId)
    {
        var order = new int[friendlyNames.Count];
        var marker = VendorMarker(pciVendorId);
        if (marker is null)
        {
            for (var i = 0; i < order.Length; i++) order[i] = i;
            return order;
        }
        var n = 0;
        for (var i = 0; i < friendlyNames.Count; i++)
            if (Matches(friendlyNames[i], marker))
                order[n++] = i;
        for (var i = 0; i < friendlyNames.Count; i++)
            if (!Matches(friendlyNames[i], marker))
                order[n++] = i;
        return order;
    }

    private static bool Matches(string? name, string marker) =>
        name?.Contains(marker, StringComparison.OrdinalIgnoreCase) == true;
}
