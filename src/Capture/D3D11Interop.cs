using System;
using System.Runtime.InteropServices;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Capture;

/// <summary>
/// d3d11.dll surface for the stream engine: device creation plus the
/// DXGI-to-WinRT projection WGC consumes. IIDs for dxgi.h/d3d11.h interfaces
/// are the stable published values (those headers are not in the local
/// transcription set); everything else is header-transcribed.
/// </summary>
internal static unsafe class D3D11Interop
{
    // d3d11.h (well-known published signature). pAdapter null + HARDWARE
    // driver type selects the default adapter; featureLevels null/0 lets the
    // runtime pick its default chain.
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        IntPtr* device,
        uint* featureLevel,
        IntPtr* immediateContext);

    // windows.graphics.directx.direct3d11.interop.h:15; exported by d3d11.dll.
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    // d3dcommon.h / d3d11.h well-known values.
    private const uint D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_SDK_VERSION = 7;
    // BGRA_SUPPORT: WGC pool format is B8G8R8A8. VIDEO_SUPPORT: required for
    // the MF DXGI device manager / video MFT sharing.
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;

    // verified against d3d11.h/dxgi.h (stable public GUIDs, not in the local
    // transcription set).
    public static readonly Guid IID_IDXGIDevice =
        new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    public static readonly Guid IID_ID3D11Device =
        new("DB6F6DDB-AC77-4E88-8253-819DF9BBF140");
    public static readonly Guid IID_ID3D11Texture2D =
        new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    // ID3D11Multithread (d3d11_4.h:1950). Enter/Leave return void;
    // SetMultithreadProtected returns the previous BOOL, not an HRESULT.
    public static readonly Guid IID_ID3D11Multithread =
        new("9B7E4E00-342C-4106-A19F-4F2704F689F0");
    public const int Multithread_Enter = 3;
    public const int Multithread_Leave = 4;
    public const int Multithread_SetMultithreadProtected = 5;
    public const int Multithread_GetMultithreadProtected = 6;

    public static int CreateHardwareDevice(out IntPtr device, out uint featureLevel)
    {
        IntPtr dev;
        uint level;
        var hr = D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT,
            IntPtr.Zero, 0, D3D11_SDK_VERSION,
            &dev, &level, null);
        device = dev;
        featureLevel = level;
        return hr;
    }

    public static int CreateWinRtDevice(IntPtr dxgiDevice, out IntPtr inspectable) =>
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable);
}

/// <summary>
/// Hardware D3D11 device shared by the whole capture-encode pipeline:
/// <see cref="Device"/> feeds the MF DXGI device manager, and
/// <see cref="WinRtDevice"/> (IDirect3DDevice) feeds the WGC frame pool.
/// The device is multithread-protected because the MF device manager and the
/// WGC free-threaded pool touch it from different threads.
/// </summary>
internal sealed unsafe class D3DDevice : IDisposable
{
    public IntPtr Device;
    public IntPtr WinRtDevice;

    private D3DDevice(IntPtr device, IntPtr winRtDevice)
    {
        Device = device;
        WinRtDevice = winRtDevice;
    }

    public static D3DDevice Create()
    {
        var hr = D3D11Interop.CreateHardwareDevice(out var device, out var featureLevel);
        if (hr < 0 || device == IntPtr.Zero)
            throw new InvalidOperationException($"D3D11CreateDevice failed 0x{hr:X8}");

        var multithread = Wv2.QueryInterface(device, D3D11Interop.IID_ID3D11Multithread);
        if (multithread == IntPtr.Zero)
        {
            Wv2.Release(device);
            throw new InvalidOperationException("ID3D11Multithread not supported by device");
        }
        var setProtected = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)
            Wv2.Slot(multithread, D3D11Interop.Multithread_SetMultithreadProtected);
        setProtected(multithread, 1);
        Wv2.Release(multithread);

        var dxgi = Wv2.QueryInterface(device, D3D11Interop.IID_IDXGIDevice);
        if (dxgi == IntPtr.Zero)
        {
            Wv2.Release(device);
            throw new InvalidOperationException("QI IDXGIDevice failed");
        }
        hr = D3D11Interop.CreateWinRtDevice(dxgi, out var inspectable);
        Wv2.Release(dxgi);
        if (hr < 0 || inspectable == IntPtr.Zero)
        {
            Wv2.Release(device);
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice failed 0x{hr:X8}");
        }
        var winRtDevice = Wv2.QueryInterface(inspectable, WgcVtable.IID_IDirect3DDevice);
        Wv2.Release(inspectable);
        if (winRtDevice == IntPtr.Zero)
        {
            Wv2.Release(device);
            throw new InvalidOperationException("QI IDirect3DDevice failed");
        }

        Log.Info($"stream-capture: d3d11 device ready (feature level 0x{featureLevel:X})");
        return new D3DDevice(device, winRtDevice);
    }

    public void Dispose()
    {
        if (WinRtDevice != IntPtr.Zero)
        {
            Wv2.Release(WinRtDevice);
            WinRtDevice = IntPtr.Zero;
        }
        if (Device != IntPtr.Zero)
        {
            Wv2.Release(Device);
            Device = IntPtr.Zero;
        }
    }
}
