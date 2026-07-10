using System;

namespace Nexus.Overlay.Capture;

/// <summary>
/// Vtable slot indices and IIDs for the Windows.Graphics.Capture surface the
/// stream engine consumes. Classic COM interfaces (the interop ones) carry
/// IUnknown at slots 0-2 with own methods from 3; WinRT interfaces add
/// IInspectable (GetIids, GetRuntimeClassName, GetTrustLevel) at 3-5 with own
/// methods from 6. All values transcribed from the 10.0.26100 SDK headers
/// named on each entry.
/// </summary>
internal static class WgcVtable
{
    // IGraphicsCaptureItemInterop (windows.graphics.capture.interop.h:15,
    // IUnknown-based).
    public static readonly Guid IID_IGraphicsCaptureItemInterop =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    public const int ItemInterop_CreateForWindow = 3;
    public const int ItemInterop_CreateForMonitor = 4;

    // IGraphicsCaptureItem (windows.graphics.capture.h:1156).
    public static readonly Guid IID_IGraphicsCaptureItem =
        new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    // IDirect3D11CaptureFramePoolStatics2 (windows.graphics.capture.h:1079);
    // CreateFreeThreaded is its only own method.
    public static readonly Guid IID_IDirect3D11CaptureFramePoolStatics2 =
        new("589b103f-6bbc-5df5-a991-02e28b3b66d5");
    public const int PoolStatics2_CreateFreeThreaded = 6;

    // IDirect3D11CaptureFramePool (windows.graphics.capture.h:983).
    public static readonly Guid IID_IDirect3D11CaptureFramePool =
        new("24eb6d22-1975-422e-82e7-780dbd8ddf24");
    public const int Pool_Recreate = 6;
    public const int Pool_TryGetNextFrame = 7;
    public const int Pool_add_FrameArrived = 8;
    public const int Pool_remove_FrameArrived = 9;
    public const int Pool_CreateCaptureSession = 10;
    public const int Pool_get_DispatcherQueue = 11;

    // IDirect3D11CaptureFrame (windows.graphics.capture.h:902).
    public static readonly Guid IID_IDirect3D11CaptureFrame =
        new("fa50c623-38da-4b32-acf3-fa9734ad800e");
    public const int Frame_get_Surface = 6;
    public const int Frame_get_SystemRelativeTime = 7;
    public const int Frame_get_ContentSize = 8;

    // IGraphicsCaptureSession (windows.graphics.capture.h:1316).
    public static readonly Guid IID_IGraphicsCaptureSession =
        new("814e42a9-f70f-4ad7-939b-fddcc6eb880d");
    public const int Session_StartCapture = 6;

    // IGraphicsCaptureSession2 (windows.graphics.capture.h:1350).
    public static readonly Guid IID_IGraphicsCaptureSession2 =
        new("2c39ae40-7d2e-5044-804e-8b6799d4cf9e");
    public const int Session2_get_IsCursorCaptureEnabled = 6;
    public const int Session2_put_IsCursorCaptureEnabled = 7;

    // IClosable (windows.foundation.h:1291).
    public static readonly Guid IID_IClosable =
        new("30d5a829-7fa4-4026-83bb-d75bae4ea99e");
    public const int Closable_Close = 6;

    // IDirect3DDxgiInterfaceAccess
    // (windows.graphics.directx.direct3d11.interop.h:28, IUnknown-based).
    public static readonly Guid IID_IDirect3DDxgiInterfaceAccess =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    public const int InterfaceAccess_GetInterface = 3;

    // IDirect3DDevice (windows.graphics.directx.direct3d11.h:328).
    public static readonly Guid IID_IDirect3DDevice =
        new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670");

    // IDirect3DSurface (windows.graphics.directx.direct3d11.h:365).
    public static readonly Guid IID_IDirect3DSurface =
        new("0bf4a146-13c1-4694-bee3-7abf15eaf586");
}
