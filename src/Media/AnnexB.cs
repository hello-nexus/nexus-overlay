using System;

namespace Nexus.Overlay.Media;

/// <summary>
/// H.264 Annex-B access-unit inspection. NAL type is the low five bits of
/// the first byte after a 3-byte (00 00 01) or 4-byte (00 00 00 01) start
/// code; type 5 is an IDR slice.
/// </summary>
internal static class AnnexB
{
    private const byte NalTypeIdr = 5;

    public static bool ContainsIdr(ReadOnlySpan<byte> accessUnit)
    {
        // i + 3 < Length: a start code with no payload byte after it is
        // truncated and carries no NAL type.
        for (int i = 0; i + 3 < accessUnit.Length; i++)
        {
            if (accessUnit[i] != 0 || accessUnit[i + 1] != 0) continue;

            int payloadStart;
            if (accessUnit[i + 2] == 1)
                payloadStart = i + 3;
            else if (accessUnit[i + 2] == 0 && i + 4 < accessUnit.Length && accessUnit[i + 3] == 1)
                payloadStart = i + 4;
            else
                continue;

            if ((accessUnit[payloadStart] & 0x1F) == NalTypeIdr) return true;
            i = payloadStart - 1;
        }
        return false;
    }
}
