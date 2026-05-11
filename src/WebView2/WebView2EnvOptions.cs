using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qos.Overlay.WebView2;

/// <summary>
/// Hand-rolled ICoreWebView2EnvironmentOptions COM impl. We only need
/// AdditionalBrowserArguments (renderer-process-limit consolidation across
/// per-monitor overlays); everything else returns defaults. 11-slot vtable
/// matching ICoreWebView2EnvironmentOptions exactly.
///
/// Object layout: [vtable_ptr][refcount_long][cotaskmem_argstr][padding]
/// </summary>
internal static unsafe class WebView2EnvOptions
{
    private const int Slot_Vtable = 0;
    private const int Slot_Refcount = 1;
    private const int Slot_ArgsString = 2;
    private const int ObjectSlotCount = 3;

    public static IntPtr Create(string additionalBrowserArguments)
    {
        var argsCopy = Marshal.StringToCoTaskMemUni(additionalBrowserArguments);

        var vt = (IntPtr*)NativeMemory.Alloc(11 * (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QI;
        vt[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        vt[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        vt[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetAdditionalBrowserArguments;
        vt[4] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutAdditionalBrowserArguments;
        vt[5] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetLanguage;
        vt[6] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutLanguage;
        vt[7] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetTargetCompatibleBrowserVersion;
        vt[8] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&PutTargetCompatibleBrowserVersion;
        vt[9] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int*, int>)&GetAllowSingleSignOnUsingOSPrimaryAccount;
        vt[10] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&PutAllowSingleSignOnUsingOSPrimaryAccount;

        var obj = (IntPtr*)NativeMemory.AllocZeroed((nuint)ObjectSlotCount * (nuint)sizeof(IntPtr));
        obj[Slot_Vtable] = (IntPtr)vt;
        obj[Slot_Refcount] = (IntPtr)1;
        obj[Slot_ArgsString] = argsCopy;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2EnvironmentOptions)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    private static uint AddRefInternal(IntPtr self)
    {
        var refSlot = (long*)((IntPtr*)self + Slot_Refcount);
        return (uint)Interlocked.Increment(ref *refSlot);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(IntPtr self) => AddRefInternal(self);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(IntPtr self)
    {
        var refSlot = (long*)((IntPtr*)self + Slot_Refcount);
        var v = (uint)Interlocked.Decrement(ref *refSlot);
        if (v == 0)
        {
            var args = ((IntPtr*)self)[Slot_ArgsString];
            if (args != IntPtr.Zero) Marshal.FreeCoTaskMem(args);
            var vt = ((IntPtr*)self)[Slot_Vtable];
            NativeMemory.Free((void*)vt);
            NativeMemory.Free((void*)self);
        }
        return v;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetAdditionalBrowserArguments(IntPtr self, IntPtr* value)
    {
        if (value == null) return WebView2Native.E_POINTER;
        var args = ((IntPtr*)self)[Slot_ArgsString];
        if (args == IntPtr.Zero) { *value = Marshal.StringToCoTaskMemUni(""); return WebView2Native.S_OK; }
        // Caller takes ownership and frees with CoTaskMemFree.
        var p = (char*)args;
        int len = 0; while (p[len] != 0) len++;
        var bytes = (len + 1) * sizeof(char);
        var copy = Marshal.AllocCoTaskMem(bytes);
        Buffer.MemoryCopy((void*)args, (void*)copy, bytes, bytes);
        *value = copy;
        return WebView2Native.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutAdditionalBrowserArguments(IntPtr self, IntPtr value) => WebView2Native.S_OK;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetLanguage(IntPtr self, IntPtr* value) { if (value != null) *value = Marshal.StringToCoTaskMemUni(""); return WebView2Native.S_OK; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutLanguage(IntPtr self, IntPtr value) => WebView2Native.S_OK;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetTargetCompatibleBrowserVersion(IntPtr self, IntPtr* value) { if (value != null) *value = Marshal.StringToCoTaskMemUni(""); return WebView2Native.S_OK; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutTargetCompatibleBrowserVersion(IntPtr self, IntPtr value) => WebView2Native.S_OK;
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int GetAllowSingleSignOnUsingOSPrimaryAccount(IntPtr self, int* value) { if (value != null) *value = 0; return WebView2Native.S_OK; }
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PutAllowSingleSignOnUsingOSPrimaryAccount(IntPtr self, int value) => WebView2Native.S_OK;
}
