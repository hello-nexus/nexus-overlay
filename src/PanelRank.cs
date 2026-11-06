using System;

namespace Nexus.Overlay;

/// <summary>Ranks display hardware ids against the known HYTE panel controllers.</summary>
internal static class PanelRank
{
    // EDID names returned by EnumDisplayDevicesW are usually just "Generic PnP
    // Monitor" - Windows doesn't surface the real friendly name through that
    // API. HYTE panels are identified instead by the hardware DeviceID (read
    // through DisplayIdentity so the ACTIVE-child rule applies here too), which
    // embeds the panel controller name and is stable per model. HYTE ships the
    // Y70 with several controllers (Realtek/BOE variants), so match any. Keep
    // in sync with Y70DisplayProtocol.DdcPanelHardwareNames in nexus-service -
    // a separate assembly, so the constant can't be shared.
    internal static readonly string[] KnownPanelHardwareNames =
    {
        "RTK2234", "RTK0004", "RTD1100", "RTK1234", "BOE2143", "RTK409A", "RTK2345",
    };

    // Lower is better; -1 is no match. The GT screen (first entry) is never used
    // alongside the Y70 touch panel, so it wins when both are connected.
    internal static int Of(string deviceId)
    {
        for (var i = 0; i < KnownPanelHardwareNames.Length; i++)
        {
            if (deviceId.IndexOf(KnownPanelHardwareNames[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return i;
            }
        }

        return -1;
    }
}
