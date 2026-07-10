using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nexus.Overlay.WebView2;

/// <summary>
/// Hand-rolled ICoreWebView2EnvironmentOptions (same allocation pattern as
/// WebView2Callbacks: NativeMemory object block = [vtable][refcount],
/// per-instance vtable, [UnmanagedCallersOnly] stubs). Sole purpose: pass
/// --disable-features=CalculateNativeWinOcclusion to the browser process -
/// Chromium occlusion-throttles rendering of the off-screen stream host
/// window without it, which starves WGC.
///
/// Vtable order from WebView2.h (Microsoft.Web.WebView2 1.0.2792.45,
/// ICoreWebView2EnvironmentOptionsVtbl): slots 3-10 are
/// get/put_AdditionalBrowserArguments, get/put_Language,
/// get/put_TargetCompatibleBrowserVersion,
/// get/put_AllowSingleSignOnUsingOSPrimaryAccount.
/// </summary>
internal static unsafe class Wv2EnvironmentOptions
{
    private const string AdditionalBrowserArguments = "--disable-features=CalculateNativeWinOcclusion";

    /// <summary>Bench diagnostics hook: NEXUS_STREAM_WV2_ARGS appends extra
    /// browser args (e.g. --remote-debugging-port=9223) to the stream env
    /// without a rebuild. Read once; the browser process consumes the args
    /// only when it first launches for the user-data folder.</summary>
    private static readonly string EffectiveBrowserArguments = BuildBrowserArguments();

    private static string BuildBrowserArguments()
    {
        var extra = Environment.GetEnvironmentVariable("NEXUS_STREAM_WV2_ARGS");
        return string.IsNullOrWhiteSpace(extra)
            ? AdditionalBrowserArguments
            : AdditionalBrowserArguments + " " + extra.Trim();
    }
    // CORE_WEBVIEW_TARGET_PRODUCT_VERSION (WebView2EnvironmentOptions.h:12,
    // package 1.0.2792.45); bump with the package.
    private const string TargetCompatibleBrowserVersion = "129.0.2792.45";

    private const int Slot_Vtable = 0;
    private const int Slot_Refcount = 1;
    private const int ObjectSlotCount = 2;
    private const int VtableSlotCount = 11;

    // Runtimes newer than the SDK QI for ICoreWebView2EnvironmentOptions2..8;
    // each unknown IID is logged once so bring-up can see which extended
    // options the runtime probed.
    private static readonly HashSet<Guid> LoggedIids = new();
    private static readonly object LoggedIidsSync = new();

    /// <summary>Owned COM pointer (refcount 1); the caller releases it after
    /// handing it to CreateCoreWebView2EnvironmentWithOptions.</summary>
    public static IntPtr Create()
    {
        var vtable = (IntPtr*)NativeMemory.Alloc(VtableSlotCount * (nuint)sizeof(IntPtr));
        vtable[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterfaceStub;
        vtable[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefStub;
        vtable[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseStub;
        vtable[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetAdditionalBrowserArgumentsStub;
        vtable[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutStringNoOpStub;
        vtable[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetLanguageStub;
        vtable[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutStringNoOpStub;
        vtable[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetTargetCompatibleBrowserVersionStub;
        vtable[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutStringNoOpStub;
        vtable[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetAllowSingleSignOnStub;
        vtable[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&PutBoolNoOpStub;

        var obj = (IntPtr*)NativeMemory.AllocZeroed((nuint)ObjectSlotCount * (nuint)sizeof(IntPtr));
        obj[Slot_Vtable] = (IntPtr)vtable;
        obj[Slot_Refcount] = 1;
        return (IntPtr)obj;
    }

    private static uint AddRefInternal(IntPtr self)
    {
        var refSlot = (long*)((IntPtr*)self + Slot_Refcount);
        return (uint)Interlocked.Increment(ref *refSlot);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRefStub(IntPtr self) => AddRefInternal(self);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint ReleaseStub(IntPtr self)
    {
        var refSlot = (long*)((IntPtr*)self + Slot_Refcount);
        var count = (uint)Interlocked.Decrement(ref *refSlot);
        if (count == 0)
        {
            var vtable = ((IntPtr*)self)[Slot_Vtable];
            NativeMemory.Free((void*)vtable);
            NativeMemory.Free((void*)self);
        }
        return count;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterfaceStub(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null || ppv == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2EnvironmentOptions)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        LogUnknownIidOnce(*iid);
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    private static void LogUnknownIidOnce(Guid iid)
    {
        lock (LoggedIidsSync)
        {
            if (!LoggedIids.Add(iid)) return;
        }
        Log.Info($"wv2-options: QI {iid:B} not implemented");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetAdditionalBrowserArgumentsStub(IntPtr self, IntPtr* value)
    {
        if (value == null) return WebView2Native.E_POINTER;
        *value = Marshal.StringToCoTaskMemUni(EffectiveBrowserArguments);
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetLanguageStub(IntPtr self, IntPtr* value)
    {
        if (value == null) return WebView2Native.E_POINTER;
        // Null means unset; the loader falls back to the OS UI language.
        *value = IntPtr.Zero;
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetTargetCompatibleBrowserVersionStub(IntPtr self, IntPtr* value)
    {
        if (value == null) return WebView2Native.E_POINTER;
        *value = Marshal.StringToCoTaskMemUni(TargetCompatibleBrowserVersion);
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetAllowSingleSignOnStub(IntPtr self, int* allow)
    {
        if (allow == null) return WebView2Native.E_POINTER;
        *allow = 0;
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutStringNoOpStub(IntPtr self, IntPtr value) => WebView2Native.S_OK;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutBoolNoOpStub(IntPtr self, int value) => WebView2Native.S_OK;
}
