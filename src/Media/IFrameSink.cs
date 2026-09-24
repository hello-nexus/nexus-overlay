using System;

namespace Nexus.Overlay.Media;

/// <summary>
/// Consumer of captured BGRA textures for one stream session. Implementations decide the
/// wire format: <see cref="MfEncoder"/> compresses to H.264, <see cref="RawFrameSink"/>
/// passes whole frames through. The host picks one from the assignment's codec and the
/// capture pump does not care which it got.
/// </summary>
internal interface IFrameSink : IDisposable
{
    /// <summary>Consumes one captured texture and releases the caller's reference to it.</summary>
    void Submit(IntPtr texture2D);
}
