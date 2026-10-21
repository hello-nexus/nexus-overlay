using System;
using System.Runtime.InteropServices;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Capture;

/// <summary>Memory IStream target for CapturePreview plus a hash of the WebView's own rendered output, which does not pass through WGC.</summary>
internal static unsafe class PreviewProbe
{
    // ObjIdl.h IStream after IUnknown: Read, Write, Seek.
    private const int Stream_Read = 3;
    private const int Stream_Seek = 5;
    private const uint StreamSeekSet = 0;
    private const uint StreamSeekEnd = 2;

    [DllImport("shlwapi.dll", EntryPoint = "SHCreateMemStream")]
    private static extern IntPtr SHCreateMemStream(IntPtr init, uint cbInit);

    public static IntPtr CreateStream() => SHCreateMemStream(IntPtr.Zero, 0);

    /// <summary>FNV-1a over the stream contents; null when the stream is unreadable or empty. Does not release the stream.</summary>
    public static ulong? Hash(IntPtr stream)
    {
        if (stream == IntPtr.Zero) return null;
        var seek = (delegate* unmanaged[Stdcall]<IntPtr, long, uint, ulong*, int>)Wv2.Slot(stream, Stream_Seek);
        var read = (delegate* unmanaged[Stdcall]<IntPtr, byte*, uint, uint*, int>)Wv2.Slot(stream, Stream_Read);
        ulong length;
        if (seek(stream, 0, StreamSeekEnd, &length) < 0 || length == 0) return null;
        ulong ignored;
        if (seek(stream, 0, StreamSeekSet, &ignored) < 0) return null;

        var hash = 14695981039346656037UL;
        var buffer = stackalloc byte[4096];
        ulong remaining = length;
        while (remaining > 0)
        {
            uint got;
            if (read(stream, buffer, 4096, &got) < 0 || got == 0) return null;
            for (uint i = 0; i < got; i++)
            {
                hash ^= buffer[i];
                hash *= 1099511628211UL;
            }
            remaining = remaining > got ? remaining - got : 0;
        }
        return hash;
    }
}
