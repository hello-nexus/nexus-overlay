using System;

namespace Nexus.Overlay.Media;

/// <summary>
/// Vtable slot indices, IIDs, attribute GUIDs, and enum values for the Media
/// Foundation surface the stream encoder consumes. Slots 0-2 are IUnknown;
/// own methods start at 3. IMFMediaType, IMFSample, IMFMediaEvent, and
/// IMFActivate extend IMFAttributes, so their own methods start at 33. All
/// values transcribed from the 10.0.26100 SDK headers named on each entry.
/// </summary>
internal static class MfVtable
{
    // ===================== interface IIDs =====================

    // mfobjects.h:310.
    public static readonly Guid IID_IMFAttributes = new("2cd2d921-c447-44a7-a13c-4adabfc247e3");
    // mfobjects.h:798.
    public static readonly Guid IID_IMFMediaBuffer = new("045FA593-8799-42b8-BC8D-8968C6453507");
    // mfobjects.h:938.
    public static readonly Guid IID_IMFSample = new("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4");
    // mfobjects.h:2123.
    public static readonly Guid IID_IMFMediaType = new("44ae0fa8-ea31-4109-8d2e-4cae4997c555");
    // mfobjects.h:4211.
    public static readonly Guid IID_IMFMediaEvent = new("DF598932-F10C-4E39-BBA2-C308F101DAA3");
    // mfobjects.h:4611.
    public static readonly Guid IID_IMFMediaEventGenerator = new("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
    // mfobjects.h:5798.
    public static readonly Guid IID_IMFActivate = new("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67");
    // mfobjects.h:6555.
    public static readonly Guid IID_IMFDXGIDeviceManager = new("eb533d5d-2db6-40f8-97a9-494692014f07");
    // mftransform.h:256.
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    // ===================== IMFAttributes (mfobjects.h:311-470) =====================
    // Shared prefix for IMFMediaType / IMFSample / IMFMediaEvent / IMFActivate.

    public const int Attr_GetUINT32 = 7;
    public const int Attr_GetGUID = 10;
    public const int Attr_GetAllocatedString = 13;
    public const int Attr_SetUINT32 = 21;
    public const int Attr_SetUINT64 = 22;
    public const int Attr_SetGUID = 24;

    // ===================== IMFSample own methods (mfobjects.h:939-982) =====================

    public const int Sample_GetSampleFlags = 33;
    public const int Sample_SetSampleFlags = 34;
    public const int Sample_GetSampleTime = 35;
    public const int Sample_SetSampleTime = 36;
    public const int Sample_GetSampleDuration = 37;
    public const int Sample_SetSampleDuration = 38;
    public const int Sample_GetBufferCount = 39;
    public const int Sample_GetBufferByIndex = 40;
    public const int Sample_ConvertToContiguousBuffer = 41;
    public const int Sample_AddBuffer = 42;

    // ===================== IMFMediaBuffer (mfobjects.h:802-820) =====================

    public const int Buffer_Lock = 3;
    public const int Buffer_Unlock = 4;
    public const int Buffer_GetCurrentLength = 5;

    // ===================== IMFMediaEvent own methods (mfobjects.h:4215) =====================

    public const int Event_GetType = 33;

    // ===================== IMFMediaEventGenerator (mfobjects.h:4615) =====================

    public const int EventGen_GetEvent = 3;
    public const int EventGen_QueueEvent = 6;

    // ===================== IMFActivate own methods (mfobjects.h:5802) =====================

    public const int Activate_ActivateObject = 33;

    // ===================== IMFDXGIDeviceManager (mfobjects.h:6556-6598) =====================

    public const int DevMgr_CloseDeviceHandle = 3;
    public const int DevMgr_GetVideoService = 4;
    public const int DevMgr_LockDevice = 5;
    public const int DevMgr_OpenDeviceHandle = 6;
    public const int DevMgr_ResetDevice = 7;
    public const int DevMgr_TestDevice = 8;
    public const int DevMgr_UnlockDevice = 9;

    // ===================== IMFTransform (mftransform.h:260-360) =====================

    public const int Xform_GetStreamCount = 4;
    public const int Xform_GetOutputStreamInfo = 7;
    public const int Xform_GetAttributes = 8;
    public const int Xform_SetInputType = 15;
    public const int Xform_SetOutputType = 16;
    public const int Xform_ProcessMessage = 23;
    public const int Xform_ProcessInput = 24;
    public const int Xform_ProcessOutput = 25;

    // ===================== media type attribute GUIDs (mfapi.h) =====================

    // mfapi.h:2607.
    public static readonly Guid MF_MT_MAJOR_TYPE = new(0x48eba18e, 0xf8c9, 0x4687, 0xbf, 0x11, 0x0a, 0x74, 0xc9, 0xf9, 0x6a, 0x8f);
    // mfapi.h:2611.
    public static readonly Guid MF_MT_SUBTYPE = new(0xf7e34c9a, 0x42e8, 0x4714, 0xb7, 0x4b, 0xcb, 0x29, 0xd7, 0x2c, 0x35, 0xe5);
    // mfapi.h:3066.
    public static readonly Guid MF_MT_FRAME_SIZE = new(0x1652c33d, 0xd6b2, 0x4012, 0xb8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xa3, 0x7d);
    // mfapi.h:3070.
    public static readonly Guid MF_MT_FRAME_RATE = new(0xc459a2e8, 0x3d2c, 0x4e44, 0xb1, 0x32, 0xfe, 0xe5, 0x15, 0x6c, 0x7b, 0xb0);
    // mfapi.h:3121.
    public static readonly Guid MF_MT_INTERLACE_MODE = new(0xe2724bb8, 0xe676, 0x4806, 0xb4, 0xb2, 0xa8, 0xd6, 0xef, 0xb4, 0x4c, 0xcd);
    // mfapi.h:3240.
    public static readonly Guid MF_MT_AVG_BITRATE = new(0x20332624, 0xfb0d, 0x4d9e, 0xbd, 0x0d, 0xcb, 0xf6, 0x78, 0x6c, 0x10, 0x2e);
    // mfapi.h:3248.
    public static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new(0xc16eb52b, 0x73a1, 0x476f, 0x8d, 0x62, 0x83, 0x9d, 0x6a, 0x02, 0x06, 0x52);
    // mfapi.h:3320.
    public static readonly Guid MF_MT_MPEG2_PROFILE = new(0xad76a80b, 0x2d5c, 0x4e0b, 0xb3, 0x75, 0x64, 0xe5, 0x20, 0x13, 0x70, 0x36);

    // ===================== media type / subtype GUIDs =====================

    // mfapi.h:3699.
    public static readonly Guid MFMediaType_Video = new(0x73646976, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    // mfapi.h:2249 via DEFINE_MEDIATYPE_GUID (mfapi.h:2218): data1 =
    // D3DFMT_A8R8G8B8 = 21 (mfapi.h:2243).
    public static readonly Guid MFVideoFormat_ARGB32 = new(0x00000015, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    // mfapi.h:2264: data1 = FCC('NV12') (byte-swapped fourcc, mfapi.h:2205).
    public static readonly Guid MFVideoFormat_NV12 = new(0x3231564E, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);
    // mfapi.h:2303: data1 = FCC('H264').
    public static readonly Guid MFVideoFormat_H264 = new(0x34363248, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    // ===================== MFT enumeration / behavior GUIDs =====================

    // mfapi.h:1872.
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new(0xf79eac7d, 0xe545, 0x4387, 0xbd, 0xee, 0xd6, 0x47, 0xd7, 0xbd, 0xe4, 0x2a);
    // mftransform.h:1641.
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new(0xe5666d6b, 0x3422, 0x4eb6, 0xa4, 0x21, 0xda, 0x7d, 0xb1, 0xf8, 0xe2, 0x07);
    // mftransform.h:1648.
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new(0x314ffbae, 0x5b41, 0x4c95, 0x9c, 0x19, 0x4e, 0x7d, 0x58, 0x6f, 0xac, 0xe3);
    // mfidl.h:18631.
    public static readonly Guid CLSID_VideoProcessorMFT = new(0x88753b26, 0x5b24, 0x49bd, 0xb2, 0xe7, 0x0c, 0x44, 0x5c, 0x78, 0xc9, 0x82);
    // mfapi.h:1133.
    public static readonly Guid MFSampleExtension_CleanPoint = new(0x9cdf01d8, 0xa0f0, 0x43ba, 0xb0, 0x77, 0xea, 0xa0, 0x6c, 0xbd, 0x72, 0x8a);

    // ===================== enum / flag values =====================

    // mfapi.h:31-40: MF_SDK_VERSION 0x0002 << 16 | MF_API_VERSION 0x0070.
    public const uint MF_VERSION = 0x00020070;
    // mfapi.h:45; no winsock init, the encoder never touches MF networking.
    public const uint MFSTARTUP_NOSOCKET = 0x1;

    // mfapi.h:2012-2018 (enum _MFT_ENUM_FLAG).
    public const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    public const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    public const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    // mftransform.h:172-180 (enum MFT_MESSAGE_TYPE).
    public const uint MFT_MESSAGE_COMMAND_FLUSH = 0;
    public const uint MFT_MESSAGE_COMMAND_DRAIN = 0x1;
    public const uint MFT_MESSAGE_SET_D3D_MANAGER = 0x2;
    public const uint MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000;
    public const uint MFT_MESSAGE_NOTIFY_END_OF_STREAM = 0x10000002;
    public const uint MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;

    // mftransform.h:139 (enum _MFT_OUTPUT_STREAM_INFO_FLAGS).
    public const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100;

    // mfobjects.h:4178-4180 (MediaEventType, METransformUnknown = 600).
    public const uint METransformNeedInput = 601;
    public const uint METransformHaveOutput = 602;

    // mferror.h (well-known published value; header not in the local
    // transcription set).
    public const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    // wtypesbase.h CLSCTX_INPROC_SERVER (well-known published value).
    public const uint CLSCTX_INPROC_SERVER = 0x1;
}
