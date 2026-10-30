using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Overlay.Media;

/// <summary>
/// Writes raw BGRA frames into the service-owned shared section for one stream session, in place
/// of a copy plus an HTTP body write. The layout mirrors the service reader (nexus-service
/// src/Panel/Streams/StreamFrameSection.cs): a 64 byte header then one frame, guarded by a
/// seqlock the writer makes odd while copying. The two must stay layout-compatible.
/// </summary>
internal sealed unsafe class SharedFrameWriter : IDisposable
{
    private const int HeaderBytes = 64;
    private const int Magic = 0x4653584E;
    private const uint FileMapWrite = 0x0002;
    private const uint EventModifyState = 0x0002;

    private readonly IntPtr _mapping;
    private readonly byte* _view;
    private readonly IntPtr _ready;
    private readonly long _capacity;
    private bool _disposed;

    private SharedFrameWriter(IntPtr mapping, byte* view, IntPtr ready, long capacity)
    {
        _mapping = mapping;
        _view = view;
        _ready = ready;
        _capacity = capacity;
    }

    /// <summary>Null when the section or its event cannot be opened; the caller stays on HTTP ingest.</summary>
    public static SharedFrameWriter? TryOpen(string sectionName, int frameBytes)
    {
        if (string.IsNullOrEmpty(sectionName) || frameBytes <= 0)
        {
            return null;
        }
        var mapping = OpenFileMappingW(FileMapWrite, 0, sectionName);
        if (mapping == IntPtr.Zero)
        {
            Log.Warn($"shared frames: OpenFileMapping failed: {Marshal.GetLastWin32Error()}");
            return null;
        }
        var capacity = HeaderBytes + (long)frameBytes;
        var view = MapViewOfFile(mapping, FileMapWrite, 0, 0, (UIntPtr)capacity);
        if (view == IntPtr.Zero)
        {
            Log.Warn($"shared frames: MapViewOfFile failed: {Marshal.GetLastWin32Error()}");
            CloseHandle(mapping);
            return null;
        }
        var ready = OpenEventW(EventModifyState, 0, sectionName + "-Ready");
        if (ready == IntPtr.Zero)
        {
            Log.Warn($"shared frames: OpenEvent failed: {Marshal.GetLastWin32Error()}");
            UnmapViewOfFile(view);
            CloseHandle(mapping);
            return null;
        }
        return new SharedFrameWriter(mapping, (byte*)view, ready, capacity);
    }

    /// <summary>
    /// Copies <paramref name="height"/> rows of <paramref name="rowBytes"/> from a mapped texture
    /// (stride <paramref name="pitch"/>) straight into the section. False when the frame does not fit.
    /// </summary>
    public bool TryWrite(byte* source, uint pitch, int width, int height)
    {
        var rowBytes = width * 4;
        var bytes = (long)rowBytes * height;
        if (_disposed || HeaderBytes + bytes > _capacity)
        {
            return false;
        }
        var sequence = Volatile.Read(ref *(long*)(_view + 8));
        if ((sequence & 1) != 0)
        {
            sequence++;
        }
        Volatile.Write(ref *(long*)(_view + 8), sequence + 1);
        Thread.MemoryBarrier();
        *(int*)(_view + 16) = width;
        *(int*)(_view + 20) = height;
        *(int*)(_view + 28) = (int)bytes;
        var destination = _view + HeaderBytes;
        if (pitch == (uint)rowBytes)
        {
            Buffer.MemoryCopy(source, destination, bytes, bytes);
        }
        else
        {
            for (var y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(source + ((long)y * pitch), destination + ((long)y * rowBytes), rowBytes, rowBytes);
            }
        }
        Thread.MemoryBarrier();
        Volatile.Write(ref *(long*)(_view + 8), sequence + 2);
        SetEvent(_ready);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        UnmapViewOfFile((IntPtr)_view);
        CloseHandle(_mapping);
        CloseHandle(_ready);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMappingW(uint desiredAccess, int inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    private static extern int UnmapViewOfFile(IntPtr baseAddress);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenEventW(uint desiredAccess, int inheritHandle, string name);

    [DllImport("kernel32.dll")]
    private static extern int SetEvent(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern int CloseHandle(IntPtr handle);
}
