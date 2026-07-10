using System;
using System.Globalization;
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

    // dxgi.h (well-known published signature).
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    // d3dcommon.h / d3d11.h well-known values. UNKNOWN is mandatory when an
    // explicit adapter is passed; HARDWARE + adapter fails E_INVALIDARG.
    private const uint D3D_DRIVER_TYPE_UNKNOWN = 0;
    private const uint D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_SDK_VERSION = 7;
    // BGRA_SUPPORT: WGC pool format is B8G8R8A8. VIDEO_SUPPORT: required for
    // the MF DXGI device manager / video MFT sharing.
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_CREATE_DEVICE_VIDEO_SUPPORT = 0x800;

    // verified against d3d11.h/dxgi.h (stable public GUIDs, not in the local
    // transcription set).
    public static readonly Guid IID_IDXGIFactory1 =
        new("770AAE78-F26F-4DBA-A829-253C83D1B387");
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

    // dxgi.h vtable order: IUnknown 0-2, IDXGIObject 3-6 (SetPrivateData,
    // SetPrivateDataInterface, GetPrivateData, GetParent), IDXGIFactory
    // EnumAdapters 7 / MakeWindowAssociation 8 / GetWindowAssociation 9 /
    // CreateSwapChain 10 / CreateSoftwareAdapter 11, IDXGIFactory1
    // EnumAdapters1 12. IDXGIAdapter EnumOutputs 7 / GetDesc 8 /
    // CheckInterfaceSupport 9, IDXGIAdapter1 GetDesc1 10.
    private const int Factory1_EnumAdapters1 = 12;
    private const int Adapter1_GetDesc1 = 10;

    // DXGI_ADAPTER_FLAG_SOFTWARE (dxgi.h).
    private const uint AdapterFlagSoftware = 2;

    // dxgierr.h; EnumAdapters1 end-of-enumeration.
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    // dxgi.h layout; LUID carried as one 64-bit field (LowPart+HighPart).
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    /// <summary>
    /// First hardware (non-software-flagged) adapter with the given PCI
    /// vendor id. The caller releases <paramref name="adapter"/>.
    /// </summary>
    public static bool TryFindAdapterByVendor(uint vendorId, out IntPtr adapter, out string description)
    {
        adapter = IntPtr.Zero;
        description = "";
        var iid = IID_IDXGIFactory1;
        var factoryHr = CreateDXGIFactory1(ref iid, out var factory);
        if (factoryHr < 0 || factory == IntPtr.Zero)
        {
            Log.Warn($"stream-capture: CreateDXGIFactory1 failed 0x{factoryHr:X8}");
            return false;
        }
        try
        {
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)
                Wv2.Slot(factory, Factory1_EnumAdapters1);
            for (uint i = 0; ; i++)
            {
                IntPtr candidate;
                var enumHr = enumAdapters1(factory, i, &candidate);
                if (enumHr < 0 || candidate == IntPtr.Zero)
                {
                    if (enumHr < 0 && enumHr != DxgiErrorNotFound)
                        Log.Warn($"stream-capture: EnumAdapters1 failed 0x{enumHr:X8}");
                    return false;
                }
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)
                    Wv2.Slot(candidate, Adapter1_GetDesc1);
                DXGI_ADAPTER_DESC1 desc = default;
                var descHr = getDesc1(candidate, &desc);
                if (descHr < 0)
                    Log.Warn($"stream-capture: adapter #{i} GetDesc1 failed 0x{descHr:X8}");
                else
                    Log.Info($"stream-capture: adapter #{i} VEN_{desc.VendorId:X4} '{new string(desc.Description)}' flags=0x{desc.Flags:X} vram={(ulong)desc.DedicatedVideoMemory / (1024 * 1024)}MB");
                if (descHr >= 0
                    && desc.VendorId == vendorId
                    && (desc.Flags & AdapterFlagSoftware) == 0)
                {
                    adapter = candidate;
                    description = new string(desc.Description);
                    return true;
                }
                Wv2.Release(candidate);
            }
        }
        finally
        {
            Wv2.Release(factory);
        }
    }

    public static int CreateHardwareDevice(IntPtr adapter, out IntPtr device, out uint featureLevel)
    {
        IntPtr dev;
        uint level;
        var hr = D3D11CreateDevice(
            adapter,
            adapter == IntPtr.Zero ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_UNKNOWN,
            IntPtr.Zero,
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

    /// <summary>PCI vendor id of the pinned adapter; 0 = default adapter.</summary>
    public uint AdapterVendorId { get; }

    private D3DDevice(IntPtr device, IntPtr winRtDevice, uint adapterVendorId)
    {
        Device = device;
        WinRtDevice = winRtDevice;
        AdapterVendorId = adapterVendorId;
    }

    public static D3DDevice Create()
    {
        var adapter = IntPtr.Zero;
        uint vendor = 0;
        // Bench hook: NEXUS_STREAM_D3D_VENDOR (PCI vendor id hex, e.g. 1002)
        // pins the stream device to that vendor's adapter instead of the
        // default (primary-display) one, so the encoder matrix can be
        // exercised per GPU on a multi-adapter box.
        var vendorEnv = Environment.GetEnvironmentVariable("NEXUS_STREAM_D3D_VENDOR");
        if (!string.IsNullOrEmpty(vendorEnv))
        {
            var hex = vendorEnv.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? vendorEnv.AsSpan(2) : vendorEnv.AsSpan();
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var requested))
            {
                Log.Warn($"stream-capture: unparseable NEXUS_STREAM_D3D_VENDOR '{vendorEnv}'; using default adapter");
            }
            else if (D3D11Interop.TryFindAdapterByVendor(requested, out adapter, out var description))
            {
                vendor = requested;
                Log.Info($"stream-capture: adapter pinned to VEN_{requested:X4} '{description}'");
            }
            else
            {
                Log.Warn($"stream-capture: no adapter with vendor id {requested:X4}; using default adapter");
            }
        }

        var pinned = adapter != IntPtr.Zero;
        var hr = D3D11Interop.CreateHardwareDevice(adapter, out var device, out var featureLevel);
        if (adapter != IntPtr.Zero) Wv2.Release(adapter);
        if (pinned && (hr < 0 || device == IntPtr.Zero))
        {
            // A ghost adapter of a removed GPU still enumerates with its
            // vendor id but cannot create a device; the machine-wide override
            // outlives bench sessions, so this must not become a permanent
            // stream-start failure.
            Log.Warn($"stream-capture: device create on pinned adapter failed 0x{hr:X8}; using default adapter");
            vendor = 0;
            hr = D3D11Interop.CreateHardwareDevice(IntPtr.Zero, out device, out featureLevel);
        }
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
        return new D3DDevice(device, winRtDevice, vendor);
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
