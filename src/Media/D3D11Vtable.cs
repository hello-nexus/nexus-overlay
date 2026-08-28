using System;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Media;

/// <summary>
/// Vtable slot indices, enums and structs for the ID3D11Device / ID3D11DeviceContext
/// calls the raw frame sink needs. Both derive from IUnknown (slots 0-2);
/// ID3D11DeviceContext additionally carries ID3D11DeviceChild (GetDevice,
/// GetPrivateData, SetPrivateData, SetPrivateDataInterface) at 3-6, so its own methods
/// start at 7. Values transcribed from the 10.0.26100 SDK d3d11.h / dxgiformat.h.
/// </summary>
internal static class D3D11Vtable
{
    // ID3D11Device (d3d11.h): CreateBuffer 3, CreateTexture1D 4, CreateTexture2D 5.
    public const int Device_CreateTexture2D = 5;
    // ID3D11Device::GetImmediateContext (d3d11.h).
    public const int Device_GetImmediateContext = 40;

    // ID3D11DeviceContext (d3d11.h): own methods start at 7 after ID3D11DeviceChild.
    public const int Context_Map = 14;
    public const int Context_Unmap = 15;
    public const int Context_CopyResource = 47;

    // dxgiformat.h DXGI_FORMAT_B8G8R8A8_UNORM; matches what WGC hands back.
    public const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

    // d3d11.h D3D11_USAGE / D3D11_CPU_ACCESS_FLAG / D3D11_MAP.
    public const uint D3D11_USAGE_STAGING = 3;
    public const uint D3D11_CPU_ACCESS_READ = 0x20000;
    public const uint D3D11_MAP_READ = 1;

    /// <summary>
    /// d3d11.h D3D11_TEXTURE2D_DESC. SampleDesc is an inline DXGI_SAMPLE_DESC
    /// (Count, Quality), flattened here so the struct stays blittable.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleDescCount;
        public uint SampleDescQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    /// <summary>d3d11.h D3D11_MAPPED_SUBRESOURCE.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_MAPPED_SUBRESOURCE
    {
        public IntPtr pData;
        public uint RowPitch;
        public uint DepthPitch;
    }
}
