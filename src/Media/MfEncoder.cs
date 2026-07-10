using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Overlay.Capture;
using Nexus.Overlay.WebView2;

namespace Nexus.Overlay.Media;

/// <summary>
/// Zero-copy Media Foundation H.264 encoder for WGC D3D11 BGRA textures:
/// BGRA texture -> Video Processor MFT (GPU BGRA->NV12) -> async hardware
/// H.264 encoder MFT -> Annex-B access units via the callback. The D3D11
/// device is shared with both MFTs through an MF DXGI device manager, so
/// frames never touch system memory until they are compressed. Port of the
/// bench-proven prototype (prototypes/artinchip-d213/d213wv2/MfEncoder.cs)
/// onto raw vtable interop.
///
/// Sample lifetime is the pipeline's throughput invariant: every IMFSample
/// is released exactly once after its handoff (vproc output after encoder
/// ProcessInput, encoder output after the byte copy, and anything dropped) -
/// a leaked sample exhausts the MFT's pool and stalls the stream at ~3
/// frames.
///
/// Submit runs on the capture pump thread; encoder events are drained on a
/// dedicated blocking-GetEvent thread. Dispose must come after the pump has
/// stopped submitting.
/// </summary>
internal sealed unsafe class MfEncoder : IDisposable
{
    private const int Nv12QueueCapacity = 4;
    // Read against the host's capture-gap warns (same threshold): output
    // gaps with clean capture arrivals isolate a stall to convert/encode.
    private const long OutputGapLogThresholdMs = 100;

    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly string _logTag;
    private readonly Action<byte[], bool> _onAccessUnit;
    private readonly IntPtr _devMgr;
    private readonly IntPtr _vproc;
    private readonly IntPtr _enc;
    private readonly IntPtr _encEvents;
    private readonly Thread _eventThread;
    private readonly BlockingCollection<IntPtr> _nv12 = new(Nv12QueueCapacity);

    private long _pts;
    private long _needIn;
    private long _lastOutputTicks;
    private long _outputGapCount;
    private long _haveOut;
    private long _submitErrs;
    private long _starved;
    private volatile bool _running = true;
    private bool _disposed;

    public MfEncoder(D3DDevice device, int width, int height, int fps, int bitrateKbps, Action<byte[], bool> onAccessUnit, string logTag = "")
    {
        _width = width;
        _height = height;
        _fps = fps;
        _logTag = logTag;
        _onAccessUnit = onAccessUnit;

        Check(MfInterop.MFStartup(MfVtable.MF_VERSION, MfVtable.MFSTARTUP_NOSOCKET), "MFStartup");
        try
        {
            Check(MfInterop.MFCreateDXGIDeviceManager(out var resetToken, out _devMgr), "MFCreateDXGIDeviceManager");
            Check(MfInterop.DevMgr_ResetDevice(_devMgr, device.Device, resetToken), "ResetDevice");

            Check(MfInterop.CoCreateInstance(
                MfVtable.CLSID_VideoProcessorMFT, IntPtr.Zero, MfVtable.CLSCTX_INPROC_SERVER,
                MfVtable.IID_IMFTransform, out _vproc), "CoCreateInstance(VideoProcessorMFT)");
            _enc = ActivateEncoder();

            Check(MfInterop.Xform_ProcessMessage(_vproc, MfVtable.MFT_MESSAGE_SET_D3D_MANAGER, _devMgr), "vproc SET_D3D_MANAGER");
            Check(MfInterop.Xform_ProcessMessage(_enc, MfVtable.MFT_MESSAGE_SET_D3D_MANAGER, _devMgr), "encoder SET_D3D_MANAGER");

            ConfigureTypes((uint)bitrateKbps * 1000);

            Check(MfInterop.Xform_ProcessMessage(_vproc, MfVtable.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "vproc BEGIN_STREAMING");
            Check(MfInterop.Xform_ProcessMessage(_vproc, MfVtable.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "vproc START_OF_STREAM");
            Check(MfInterop.Xform_ProcessMessage(_enc, MfVtable.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero), "encoder BEGIN_STREAMING");
            Check(MfInterop.Xform_ProcessMessage(_enc, MfVtable.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero), "encoder START_OF_STREAM");

            _encEvents = Wv2.QueryInterface(_enc, MfVtable.IID_IMFMediaEventGenerator);
            if (_encEvents == IntPtr.Zero)
                throw new InvalidOperationException("encoder MFT has no IMFMediaEventGenerator");
        }
        catch
        {
            // A construction failure must not leak the startup ref or the
            // half-built graph: the faulted host respawns every reconcile,
            // so a leak here compounds indefinitely on a failing box, and
            // each leaked startup ref turns every later encoder Dispose into
            // the leak-on-timeout path.
            if (_encEvents != IntPtr.Zero) Wv2.Release(_encEvents);
            if (_enc != IntPtr.Zero) Wv2.Release(_enc);
            if (_vproc != IntPtr.Zero) Wv2.Release(_vproc);
            if (_devMgr != IntPtr.Zero) Wv2.Release(_devMgr);
            MfInterop.MFShutdown();
            throw;
        }

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mf-encoder-events" };
        _eventThread.Start();
        Log.Info($"stream-encoder: pipeline ready {_width}x{_height}@{_fps} {bitrateKbps}kbps");
    }

    /// <summary>
    /// Feeds one BGRA ID3D11Texture2D through the vproc and queues the NV12
    /// output for the encoder's need-input events. Takes ownership of
    /// <paramref name="texture2D"/>: it is released once the vproc
    /// ProcessInput path completes or the frame is dropped.
    /// </summary>
    public void Submit(IntPtr texture2D)
    {
        if (!_running)
        {
            Wv2.Release(texture2D);
            return;
        }

        var buffer = IntPtr.Zero;
        var sample = IntPtr.Zero;
        try
        {
            var hr = MfInterop.MFCreateDXGISurfaceBuffer(
                D3D11Interop.IID_ID3D11Texture2D, texture2D, 0, 0, out buffer);
            if (hr < 0) { LogSubmitError("MFCreateDXGISurfaceBuffer", hr); return; }
            hr = MfInterop.MFCreateSample(out sample);
            if (hr < 0) { LogSubmitError("MFCreateSample", hr); return; }
            hr = MfInterop.Sample_AddBuffer(sample, buffer);
            if (hr < 0) { LogSubmitError("AddBuffer", hr); return; }

            // 10MHz (100ns) units; monotonic pts at the nominal frame cadence.
            var duration = 10_000_000L / _fps;
            MfInterop.Sample_SetSampleTime(sample, _pts);
            MfInterop.Sample_SetSampleDuration(sample, duration);
            _pts += duration;

            hr = MfInterop.Xform_ProcessInput(_vproc, 0, sample, 0);
            if (hr < 0) { LogSubmitError("vproc ProcessInput", hr); return; }
        }
        finally
        {
            // The vproc AddRef'd what it needs; dropping our refs lets its
            // sample pool recycle.
            if (sample != IntPtr.Zero) Wv2.Release(sample);
            if (buffer != IntPtr.Zero) Wv2.Release(buffer);
            Wv2.Release(texture2D);
        }

        DrainVproc();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        // Injected event wakes the blocked GetEvent deterministically.
        // MFShutdown cannot: it only fails GetEvent when the process-wide
        // startup refcount hits zero, which is false whenever another stream
        // session is live, and the Join below would then stall the message
        // loop for its full timeout on every single-session close.
        MfInterop.EventGen_QueueEvent(_encEvents, MfVtable.MEError);
        if (!_eventThread.Join(5000))
        {
            // The encoder objects the thread may still touch are leaked
            // deliberately rather than released under a live call.
            Log.Warn("stream-encoder: event thread did not exit; leaking encoder refs");
            DrainQueue();
            MfInterop.MFShutdown();
            return;
        }
        DrainQueue();
        if (_encEvents != IntPtr.Zero) Wv2.Release(_encEvents);
        if (_enc != IntPtr.Zero) Wv2.Release(_enc);
        if (_vproc != IntPtr.Zero) Wv2.Release(_vproc);
        if (_devMgr != IntPtr.Zero) Wv2.Release(_devMgr);
        MfInterop.MFShutdown();
        Log.Info("stream-encoder: disposed");
    }

    // ===================== construction =====================

    private IntPtr ActivateEncoder()
    {
        var outputType = new MFT_REGISTER_TYPE_INFO
        {
            GuidMajorType = MfVtable.MFMediaType_Video,
            GuidSubtype = MfVtable.MFVideoFormat_H264,
        };
        Check(MfInterop.MFTEnumEx(
            MfVtable.MFT_CATEGORY_VIDEO_ENCODER,
            MfVtable.MFT_ENUM_FLAG_HARDWARE | MfVtable.MFT_ENUM_FLAG_ASYNCMFT | MfVtable.MFT_ENUM_FLAG_SORTANDFILTER,
            null, &outputType, out var activates, out var count), "MFTEnumEx");
        try
        {
            Log.Info($"stream-encoder: MFTEnumEx count={count}");
            if (count == 0)
                throw new InvalidOperationException("no hardware async H.264 encoder MFT");

            var lastHr = 0;
            for (uint i = 0; i < count; i++)
            {
                // The top-ranked activate can fail (bench-hit: E_OUTOFMEMORY);
                // fall through to the next one.
                var activate = ((IntPtr*)activates)[i];
                var hr = MfInterop.Activate_ActivateObject(activate, MfVtable.IID_IMFTransform, out var enc);
                if (hr < 0 || enc == IntPtr.Zero)
                {
                    lastHr = hr;
                    Log.Warn($"stream-encoder: MFT #{i} activate failed 0x{hr:X8}");
                    continue;
                }
                // Async MFTs reject all use until the unlock attribute is set.
                var attrHr = MfInterop.Xform_GetAttributes(enc, out var attrs);
                if (attrHr >= 0 && attrs != IntPtr.Zero)
                {
                    attrHr = MfInterop.Attr_SetUINT32(attrs, MfVtable.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                    Wv2.Release(attrs);
                }
                if (attrHr < 0)
                {
                    lastHr = attrHr;
                    Log.Warn($"stream-encoder: MFT #{i} async unlock failed 0x{attrHr:X8}");
                    Wv2.Release(enc);
                    continue;
                }
                MfInterop.Attr_GetAllocatedString(activate, MfVtable.MFT_FRIENDLY_NAME_Attribute, out var name);
                Log.Info($"stream-encoder: activated MFT #{i} '{name ?? "unknown"}'");
                return enc;
            }
            throw new InvalidOperationException($"all {count} hardware encoder MFTs failed; last hr=0x{lastHr:X8}");
        }
        finally
        {
            for (uint i = 0; i < count; i++)
            {
                var activate = ((IntPtr*)activates)[i];
                if (activate != IntPtr.Zero) Wv2.Release(activate);
            }
            Marshal.FreeCoTaskMem(activates);
        }
    }

    private void ConfigureTypes(uint bitrateBps)
    {
        var type = CreateVideoType(MfVtable.MFVideoFormat_ARGB32);
        try { Check(MfInterop.Xform_SetInputType(_vproc, 0, type, 0), "vproc SetInputType(ARGB32)"); }
        finally { Wv2.Release(type); }

        type = CreateVideoType(MfVtable.MFVideoFormat_NV12);
        try { Check(MfInterop.Xform_SetOutputType(_vproc, 0, type, 0), "vproc SetOutputType(NV12)"); }
        finally { Wv2.Release(type); }

        // Output type before input type: the H.264 encoder MFT requires it.
        type = CreateVideoType(MfVtable.MFVideoFormat_H264);
        try
        {
            Check(MfInterop.Attr_SetUINT32(type, MfVtable.MF_MT_AVG_BITRATE, bitrateBps), "set MF_MT_AVG_BITRATE");
            // 2 = MFVideoInterlace_Progressive.
            Check(MfInterop.Attr_SetUINT32(type, MfVtable.MF_MT_INTERLACE_MODE, 2), "set MF_MT_INTERLACE_MODE");
            // 66 = eAVEncH264VProfile_Base; the wire consumers decode
            // baseline only.
            Check(MfInterop.Attr_SetUINT32(type, MfVtable.MF_MT_MPEG2_PROFILE, 66), "set MF_MT_MPEG2_PROFILE");
            // IDR every 2s so a wire hiccup or missed reference self-repairs
            // instead of smearing for the whole GOP.
            Check(MfInterop.Attr_SetUINT32(type, MfVtable.MF_MT_MAX_KEYFRAME_SPACING, (uint)(_fps * 2)), "set MF_MT_MAX_KEYFRAME_SPACING");
            Check(MfInterop.Xform_SetOutputType(_enc, 0, type, 0), "encoder SetOutputType(H264)");
        }
        finally { Wv2.Release(type); }

        type = CreateVideoType(MfVtable.MFVideoFormat_NV12);
        try
        {
            Check(MfInterop.Attr_SetUINT32(type, MfVtable.MF_MT_INTERLACE_MODE, 2), "set encoder input MF_MT_INTERLACE_MODE");
            Check(MfInterop.Xform_SetInputType(_enc, 0, type, 0), "encoder SetInputType(NV12)");
        }
        finally { Wv2.Release(type); }
    }

    private IntPtr CreateVideoType(in Guid subtype)
    {
        Check(MfInterop.MFCreateMediaType(out var type), "MFCreateMediaType");
        Check(MfInterop.Attr_SetGUID(type, MfVtable.MF_MT_MAJOR_TYPE, MfVtable.MFMediaType_Video), "set MF_MT_MAJOR_TYPE");
        Check(MfInterop.Attr_SetGUID(type, MfVtable.MF_MT_SUBTYPE, subtype), "set MF_MT_SUBTYPE");
        Check(MfInterop.Attr_SetUINT64(type, MfVtable.MF_MT_FRAME_SIZE, ((ulong)(uint)_width << 32) | (uint)_height), "set MF_MT_FRAME_SIZE");
        Check(MfInterop.Attr_SetUINT64(type, MfVtable.MF_MT_FRAME_RATE, ((ulong)(uint)_fps << 32) | 1u), "set MF_MT_FRAME_RATE");
        return type;
    }

    // ===================== vproc output -> NV12 queue =====================

    private void DrainVproc()
    {
        while (true)
        {
            if (MfInterop.Xform_GetOutputStreamInfo(_vproc, 0, out var info) < 0) return;
            var db = default(MFT_OUTPUT_DATA_BUFFER);
            var suppliedSample = false;
            // A D3D-backed MFT allocates its own output sample; supply one
            // only when it does not (else E_INVALIDARG).
            if ((info.dwFlags & MfVtable.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0)
            {
                if (MfInterop.MFCreateSample(out var s) < 0) return;
                if (MfInterop.MFCreateMemoryBuffer(info.cbSize, out var mem) >= 0)
                {
                    MfInterop.Sample_AddBuffer(s, mem);
                    Wv2.Release(mem);
                }
                db.pSample = s;
                suppliedSample = true;
            }

            var hr = MfInterop.Xform_ProcessOutput(_vproc, 0, 1, &db, out _);
            if (db.pEvents != IntPtr.Zero)
            {
                Wv2.Release(db.pEvents);
                db.pEvents = IntPtr.Zero;
            }
            if (hr == MfVtable.MF_E_TRANSFORM_NEED_MORE_INPUT || hr < 0)
            {
                if (hr != MfVtable.MF_E_TRANSFORM_NEED_MORE_INPUT)
                    LogSubmitError("vproc ProcessOutput", hr);
                if (suppliedSample && db.pSample != IntPtr.Zero) Wv2.Release(db.pSample);
                return;
            }
            if (db.pSample == IntPtr.Zero) return;

            EnqueueNv12(db.pSample);
        }
    }

    private void EnqueueNv12(IntPtr sample)
    {
        if (!_running)
        {
            Wv2.Release(sample);
            return;
        }
        if (_nv12.TryAdd(sample)) return;
        // Drop-oldest keeps latency bounded when the encoder falls behind;
        // the dropped sample must be released to free its pool slot.
        if (_nv12.TryTake(out var oldest)) Wv2.Release(oldest);
        if (!_nv12.TryAdd(sample)) Wv2.Release(sample);
    }

    // ===================== encoder event loop =====================

    private void EventLoop()
    {
        while (_running)
        {
            var hr = MfInterop.EventGen_GetEvent(_encEvents, 0, out var mediaEvent);
            if (hr < 0 || mediaEvent == IntPtr.Zero)
            {
                Log.Info($"stream-encoder: event loop ended 0x{hr:X8}");
                break;
            }
            try
            {
                if (MfInterop.Event_GetType(mediaEvent, out var eventType) < 0) continue;
                if (eventType == MfVtable.METransformNeedInput) OnNeedInput();
                else if (eventType == MfVtable.METransformHaveOutput) OnHaveOutput();
            }
            catch (Exception ex)
            {
                // A background-thread exception would kill the process.
                Log.Error($"stream-encoder: event handler failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Wv2.Release(mediaEvent);
            }
        }
    }

    private void OnNeedInput()
    {
        if (++_needIn <= 3) Log.Info($"stream-encoder: need-input #{_needIn}");
        if (!_nv12.TryTake(out var sample, 2000))
        {
            // Starvation is normal on a static page (WGC emits no frames);
            // log sparsely so an idle panel does not flood the file log.
            var starved = ++_starved;
            if (starved <= 3 || starved % 300 == 0)
                Log.Warn($"stream-encoder: input queue empty (starved x{starved})");
            return;
        }
        var hr = MfInterop.Xform_ProcessInput(_enc, 0, sample, 0);
        // The encoder AddRef'd the sample; dropping our ref recycles the
        // vproc pool slot.
        Wv2.Release(sample);
        if (hr < 0) LogSubmitError("encoder ProcessInput", hr);
    }

    private void OnHaveOutput()
    {
        if (MfInterop.Xform_GetOutputStreamInfo(_enc, 0, out var info) < 0) return;
        var db = default(MFT_OUTPUT_DATA_BUFFER);
        var suppliedSample = false;
        if ((info.dwFlags & MfVtable.MFT_OUTPUT_STREAM_PROVIDES_SAMPLES) == 0)
        {
            if (MfInterop.MFCreateSample(out var s) < 0) return;
            if (MfInterop.MFCreateMemoryBuffer(Math.Max(info.cbSize, 1u << 20), out var mem) >= 0)
            {
                MfInterop.Sample_AddBuffer(s, mem);
                Wv2.Release(mem);
            }
            db.pSample = s;
            suppliedSample = true;
        }

        var hr = MfInterop.Xform_ProcessOutput(_enc, 0, 1, &db, out _);
        if (++_haveOut <= 3) Log.Info($"stream-encoder: have-output #{_haveOut} hr=0x{hr:X8}");
        if (db.pEvents != IntPtr.Zero)
        {
            Wv2.Release(db.pEvents);
            db.pEvents = IntPtr.Zero;
        }
        if (hr < 0 || db.pSample == IntPtr.Zero)
        {
            if (suppliedSample && db.pSample != IntPtr.Zero) Wv2.Release(db.pSample);
            return;
        }

        var sample = db.pSample;
        byte[]? accessUnit = null;
        var isIdr = false;
        if (MfInterop.Sample_ConvertToContiguousBuffer(sample, out var buffer) >= 0 && buffer != IntPtr.Zero)
        {
            if (MfInterop.Buffer_Lock(buffer, out var data, out var length) >= 0)
            {
                accessUnit = new byte[length];
                new ReadOnlySpan<byte>(data, (int)length).CopyTo(accessUnit);
                MfInterop.Buffer_Unlock(buffer);
            }
            Wv2.Release(buffer);
        }
        if (accessUnit is { Length: > 0 })
        {
            // CleanPoint marks the sample as a safe decode entry (IDR);
            // absent attribute falls back to scanning the NAL units.
            isIdr = MfInterop.Attr_GetUINT32(sample, MfVtable.MFSampleExtension_CleanPoint, out var clean) >= 0
                ? clean == 1
                : AnnexB.ContainsIdr(accessUnit);
        }
        // Recycle the encoder's pool slot before invoking the callback.
        Wv2.Release(sample);

        if (accessUnit is { Length: > 0 })
        {
            // Change-driven capture makes output gaps routine on slow
            // content; sparse-log like the starvation counter above.
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastOutputTicks != 0)
            {
                var gapMs = (now - _lastOutputTicks) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                if (gapMs > OutputGapLogThresholdMs)
                {
                    var count = ++_outputGapCount;
                    if (count <= 3 || count % 100 == 0)
                        Log.Warn($"stream-encoder {_logTag}: output gap {gapMs}ms (x{count})");
                }
            }
            _lastOutputTicks = now;
            _onAccessUnit(accessUnit, isIdr);
        }
    }

    // ===================== helpers =====================

    private void DrainQueue()
    {
        while (_nv12.TryTake(out var sample)) Wv2.Release(sample);
    }

    private void LogSubmitError(string stage, int hr)
    {
        if (Interlocked.Increment(ref _submitErrs) <= 5)
            Log.Error($"stream-encoder: {stage} failed 0x{hr:X8}");
    }

    private static void Check(int hr, string stage)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{stage} failed 0x{hr:X8}");
    }
}
