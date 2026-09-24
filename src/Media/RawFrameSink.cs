using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Media;

/// <summary>
/// Uncompressed frame sink for panels that take a framebuffer rather than a video
/// stream: the captured BGRA texture is copied into a CPU-readable staging texture and
/// handed to the callback as tightly packed rows.
///
/// It stands in for <see cref="MfEncoder"/> and keeps the same Submit/callback shape, so
/// the host swaps one for the other on the assignment's codec and nothing else changes.
/// Only viable for a small, slow panel: a 640x640 frame is 1.6 MB, where H.264 would be
/// a few tens of KB.
///
/// Submit runs on the capture pump thread. The consumer (IngestClient) queues what it is
/// handed without copying, so a frame's array is only reused after the consumer passes it
/// back through <see cref="Return"/> once its bytes are sent.
/// </summary>
internal sealed unsafe class RawFrameSink : IFrameSink
{
    private readonly int _width;
    private readonly int _height;
    private readonly string _logTag;
    private readonly Action<byte[], bool> _onFrame;
    private readonly IntPtr _context;

    // Frames are megabytes each; enough spares to cover the few a send can have in flight.
    private const int MaxSpareFrames = 4;
    private readonly ConcurrentQueue<byte[]> _spare = new();

    private IntPtr _staging;
    private bool _disposed;

    public RawFrameSink(D3DDevice device, int width, int height, Action<byte[], bool> onFrame, string logTag)
    {
        _width = width;
        _height = height;
        _onFrame = onFrame;
        _logTag = logTag;

        var getContext = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, void>)
            Wv2.Slot(device.Device, D3D11Vtable.Device_GetImmediateContext);
        IntPtr ctx;
        getContext(device.Device, &ctx);
        if (ctx == IntPtr.Zero)
        {
            throw new InvalidOperationException("ID3D11Device::GetImmediateContext returned null");
        }
        _context = ctx;

        _staging = CreateStaging(device.Device, width, height);
    }

    private static IntPtr CreateStaging(IntPtr device, int width, int height)
    {
        var desc = new D3D11Vtable.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = D3D11Vtable.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDescCount = 1,
            SampleDescQuality = 0,
            Usage = D3D11Vtable.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11Vtable.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };
        var create = (delegate* unmanaged[Stdcall]<IntPtr, D3D11Vtable.D3D11_TEXTURE2D_DESC*, IntPtr, IntPtr*, int>)
            Wv2.Slot(device, D3D11Vtable.Device_CreateTexture2D);
        IntPtr tex;
        var hr = create(device, &desc, IntPtr.Zero, &tex);
        if (hr < 0 || tex == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateTexture2D (staging) failed: 0x{hr:X8}");
        }
        return tex;
    }

    /// <summary>Hands back a frame whose bytes have been sent, for a later Submit to fill.</summary>
    public void Return(byte[] frame)
    {
        if (frame.Length == _width * 4 * _height && _spare.Count < MaxSpareFrames)
        {
            _spare.Enqueue(frame);
        }
    }

    /// <summary>
    /// Copies one captured texture out and emits it. Releases the caller's texture
    /// reference, as the encoder's Submit does.
    /// </summary>
    public void Submit(IntPtr texture)
    {
        if (texture == IntPtr.Zero)
        {
            return;
        }
        try
        {
            SubmitCore(texture);
        }
        finally
        {
            Wv2.Release(texture);
        }
    }

    private void SubmitCore(IntPtr texture)
    {
        if (_disposed || _staging == IntPtr.Zero)
        {
            return;
        }

        var copy = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)
            Wv2.Slot(_context, D3D11Vtable.Context_CopyResource);
        copy(_context, _staging, texture);

        var map = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, D3D11Vtable.D3D11_MAPPED_SUBRESOURCE*, int>)
            Wv2.Slot(_context, D3D11Vtable.Context_Map);
        D3D11Vtable.D3D11_MAPPED_SUBRESOURCE mapped;
        var hr = map(_context, _staging, 0, D3D11Vtable.D3D11_MAP_READ, 0, &mapped);
        if (hr < 0 || mapped.pData == IntPtr.Zero)
        {
            Log.Error($"{_logTag}: staging Map failed: 0x{hr:X8}");
            return;
        }
        int rowBytes = _width * 4;
        if (!_spare.TryDequeue(out var frame))
        {
            frame = new byte[rowBytes * _height];
        }
        try
        {
            // RowPitch is the driver's stride and is >= width*4; copy row by row so the
            // callback always receives tightly packed rows.
            var src = (byte*)mapped.pData;
            fixed (byte* dst = frame)
            {
                if (mapped.RowPitch == (uint)rowBytes)
                {
                    Buffer.MemoryCopy(src, dst, frame.Length, frame.Length);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        Buffer.MemoryCopy(src + ((long)y * mapped.RowPitch), dst + ((long)y * rowBytes), rowBytes, rowBytes);
                    }
                }
            }
        }
        finally
        {
            var unmap = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)
                Wv2.Slot(_context, D3D11Vtable.Context_Unmap);
            unmap(_context, _staging, 0);
        }

        // Every raw frame stands alone, so each one is a keyframe.
        _onFrame(frame, true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_staging != IntPtr.Zero)
        {
            Marshal.Release(_staging);
            _staging = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            Marshal.Release(_context);
        }
    }
}
