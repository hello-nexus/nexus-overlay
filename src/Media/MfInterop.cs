using System;
using System.Runtime.InteropServices;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Media;

/// <summary>
/// MFT_REGISTER_TYPE_INFO (mfobjects.h:2971-2975).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_REGISTER_TYPE_INFO
{
    public Guid GuidMajorType;
    public Guid GuidSubtype;
}

/// <summary>
/// MFT_OUTPUT_DATA_BUFFER (mftransform.h:205-211). pEvents is an
/// IMFCollection the MFT may attach; the caller releases it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_DATA_BUFFER
{
    public uint dwStreamID;
    public IntPtr pSample;
    public uint dwStatus;
    public IntPtr pEvents;
}

/// <summary>
/// MFT_OUTPUT_STREAM_INFO (mftransform.h:198-203).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MFT_OUTPUT_STREAM_INFO
{
    public uint dwFlags;
    public uint cbSize;
    public uint cbAlignment;
}

/// <summary>
/// mfplat.dll / ole32.dll exports plus typed vtable-call helpers for the
/// Media Foundation methods the stream encoder uses. Slot indices and
/// signatures come from MfVtable's header transcription. AOT-safe:
/// blittable-only signatures, raw function-pointer dispatch.
/// </summary>
internal static unsafe class MfInterop
{
    // ===================== mfplat.dll exports (mfapi.h) =====================

    // mfapi.h:80.
    [DllImport("mfplat.dll")]
    public static extern int MFStartup(uint version, uint flags);

    // mfapi.h:96.
    [DllImport("mfplat.dll")]
    public static extern int MFShutdown();

    // mfapi.h:561.
    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IntPtr deviceManager);

    // mfapi.h:3798.
    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IntPtr mediaType);

    // mfapi.h:897.
    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IntPtr sample);

    // mfapi.h:457.
    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    // mfapi.h:535.
    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGISurfaceBuffer(
        in Guid riid, IntPtr surface, uint subresourceIndex, int bottomUpWhenLinear, out IntPtr buffer);

    // mfapi.h:2032. activates is an IMFActivate** array: CoTaskMemFree the
    // array, Release each element.
    [DllImport("mfplat.dll")]
    public static extern int MFTEnumEx(
        Guid category, uint flags,
        MFT_REGISTER_TYPE_INFO* inputType, MFT_REGISTER_TYPE_INFO* outputType,
        out IntPtr activates, out uint count);

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint clsContext, in Guid iid, out IntPtr instance);

    // ===================== IMFAttributes-layout helpers =====================
    // Valid on IMFAttributes, IMFMediaType, IMFSample, IMFMediaEvent, and
    // IMFActivate pointers (shared vtable prefix).

    public static int Attr_GetUINT32(IntPtr attrs, in Guid key, out uint value)
    {
        uint v;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Wv2.Slot(attrs, MfVtable.Attr_GetUINT32);
        int hr;
        fixed (Guid* k = &key) hr = fn(attrs, k, &v);
        value = v;
        return hr;
    }

    /// <summary>Returned string is CoTaskMem-owned; freed here after copy.</summary>
    public static int Attr_GetAllocatedString(IntPtr attrs, in Guid key, out string? value)
    {
        IntPtr p;
        uint length;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)Wv2.Slot(attrs, MfVtable.Attr_GetAllocatedString);
        int hr;
        fixed (Guid* k = &key) hr = fn(attrs, k, &p, &length);
        if (hr < 0 || p == IntPtr.Zero)
        {
            value = null;
            return hr;
        }
        value = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return hr;
    }

    public static int Attr_SetUINT32(IntPtr attrs, in Guid key, uint value)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Wv2.Slot(attrs, MfVtable.Attr_SetUINT32);
        fixed (Guid* k = &key) return fn(attrs, k, value);
    }

    public static int Attr_SetUINT64(IntPtr attrs, in Guid key, ulong value)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Wv2.Slot(attrs, MfVtable.Attr_SetUINT64);
        fixed (Guid* k = &key) return fn(attrs, k, value);
    }

    public static int Attr_SetGUID(IntPtr attrs, in Guid key, in Guid value)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Wv2.Slot(attrs, MfVtable.Attr_SetGUID);
        fixed (Guid* k = &key)
        fixed (Guid* v = &value)
            return fn(attrs, k, v);
    }

    // ===================== IMFSample =====================

    public static int Sample_SetSampleTime(IntPtr sample, long hns)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Wv2.Slot(sample, MfVtable.Sample_SetSampleTime);
        return fn(sample, hns);
    }

    public static int Sample_SetSampleDuration(IntPtr sample, long hns)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Wv2.Slot(sample, MfVtable.Sample_SetSampleDuration);
        return fn(sample, hns);
    }

    public static int Sample_ConvertToContiguousBuffer(IntPtr sample, out IntPtr buffer)
    {
        IntPtr b;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Wv2.Slot(sample, MfVtable.Sample_ConvertToContiguousBuffer);
        var hr = fn(sample, &b);
        buffer = b;
        return hr;
    }

    public static int Sample_AddBuffer(IntPtr sample, IntPtr buffer)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Wv2.Slot(sample, MfVtable.Sample_AddBuffer);
        return fn(sample, buffer);
    }

    // ===================== IMFMediaBuffer =====================

    public static int Buffer_Lock(IntPtr buffer, out byte* data, out uint currentLength)
    {
        byte* p;
        uint max, current;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Wv2.Slot(buffer, MfVtable.Buffer_Lock);
        var hr = fn(buffer, &p, &max, &current);
        data = p;
        currentLength = current;
        return hr;
    }

    public static int Buffer_Unlock(IntPtr buffer)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Wv2.Slot(buffer, MfVtable.Buffer_Unlock);
        return fn(buffer);
    }

    // ===================== IMFMediaEvent / IMFMediaEventGenerator =====================

    public static int Event_GetType(IntPtr mediaEvent, out uint eventType)
    {
        uint t;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Wv2.Slot(mediaEvent, MfVtable.Event_GetType);
        var hr = fn(mediaEvent, &t);
        eventType = t;
        return hr;
    }

    /// <summary>Blocks with flags 0 until the MFT queues an event.</summary>
    public static int EventGen_GetEvent(IntPtr generator, uint flags, out IntPtr mediaEvent)
    {
        IntPtr ev;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Wv2.Slot(generator, MfVtable.EventGen_GetEvent);
        var hr = fn(generator, flags, &ev);
        mediaEvent = ev;
        return hr;
    }

    // ===================== IMFActivate =====================

    public static int Activate_ActivateObject(IntPtr activate, in Guid iid, out IntPtr instance)
    {
        IntPtr o;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Wv2.Slot(activate, MfVtable.Activate_ActivateObject);
        int hr;
        fixed (Guid* i = &iid) hr = fn(activate, i, &o);
        instance = o;
        return hr;
    }

    // ===================== IMFDXGIDeviceManager =====================

    public static int DevMgr_ResetDevice(IntPtr manager, IntPtr d3dDevice, uint resetToken)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)Wv2.Slot(manager, MfVtable.DevMgr_ResetDevice);
        return fn(manager, d3dDevice, resetToken);
    }

    // ===================== IMFTransform =====================

    public static int Xform_GetOutputStreamInfo(IntPtr transform, uint streamId, out MFT_OUTPUT_STREAM_INFO info)
    {
        MFT_OUTPUT_STREAM_INFO i;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, MFT_OUTPUT_STREAM_INFO*, int>)Wv2.Slot(transform, MfVtable.Xform_GetOutputStreamInfo);
        var hr = fn(transform, streamId, &i);
        info = i;
        return hr;
    }

    public static int Xform_GetAttributes(IntPtr transform, out IntPtr attributes)
    {
        IntPtr a;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Wv2.Slot(transform, MfVtable.Xform_GetAttributes);
        var hr = fn(transform, &a);
        attributes = a;
        return hr;
    }

    public static int Xform_SetInputType(IntPtr transform, uint streamId, IntPtr mediaType, uint flags)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Wv2.Slot(transform, MfVtable.Xform_SetInputType);
        return fn(transform, streamId, mediaType, flags);
    }

    public static int Xform_SetOutputType(IntPtr transform, uint streamId, IntPtr mediaType, uint flags)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Wv2.Slot(transform, MfVtable.Xform_SetOutputType);
        return fn(transform, streamId, mediaType, flags);
    }

    /// <summary>ulParam is ULONG_PTR: the raw device-manager pointer for
    /// SET_D3D_MANAGER, zero for the notify messages.</summary>
    public static int Xform_ProcessMessage(IntPtr transform, uint message, IntPtr param)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Wv2.Slot(transform, MfVtable.Xform_ProcessMessage);
        return fn(transform, message, param);
    }

    public static int Xform_ProcessInput(IntPtr transform, uint streamId, IntPtr sample, uint flags)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Wv2.Slot(transform, MfVtable.Xform_ProcessInput);
        return fn(transform, streamId, sample, flags);
    }

    public static int Xform_ProcessOutput(IntPtr transform, uint flags, uint bufferCount, MFT_OUTPUT_DATA_BUFFER* buffers, out uint status)
    {
        uint s;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, MFT_OUTPUT_DATA_BUFFER*, uint*, int>)Wv2.Slot(transform, MfVtable.Xform_ProcessOutput);
        var hr = fn(transform, flags, bufferCount, buffers, &s);
        status = s;
        return hr;
    }
}
