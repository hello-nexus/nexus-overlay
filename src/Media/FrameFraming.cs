using System;
using System.Buffers.Binary;

namespace Nexus.Overlay.Media;

/// <summary>
/// Wire framing for the overlay -> service ingest stream. One frame is
/// <c>u32_le payloadLength | u8 flags | payload</c>; the payload is exactly
/// one H.264 Annex-B access unit. Mirrors the service-side reader contract
/// (nexus-service src/Panel/Streams/StreamFraming.cs); the two must stay
/// byte-compatible.
/// </summary>
internal static class FrameFraming
{
    public const int HeaderSize = 5;

    /// <summary>Payload is an IDR access unit (safe resync point).</summary>
    public const byte FlagIdr = 0x01;

    /// <summary>Zero-length keepalive; proves ingest liveness, never enqueued.</summary>
    public const byte FlagControl = 0x02;

    public static void WriteHeader(Span<byte> destination, int payloadLength, byte flags)
    {
        if (destination.Length < HeaderSize)
            throw new ArgumentException($"destination holds {destination.Length} bytes; header needs {HeaderSize}", nameof(destination));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)payloadLength);
        destination[4] = flags;
    }
}
