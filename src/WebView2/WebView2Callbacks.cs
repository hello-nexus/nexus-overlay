using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qos.Overlay.WebView2;

/// <summary>
/// Hand-rolled COM-callable wrappers. We allocate an unmanaged block laid
/// out as <c>[vtable_ptr][refcount_long][user_data_ptr]</c> and pass that
/// IntPtr to WebView2. Static <c>[UnmanagedCallersOnly]</c> stubs at the
/// vtable slots implement IUnknown + Invoke / each event signature.
///
/// Lifetime: WebView2 holds its own AddRef on these. Once we hand the
/// pointer over we never call Release ourselves - WebView2 cleans up when
/// it removes the handler. We do AddRef once at creation so the initial
/// refcount is 1.
/// </summary>
internal static unsafe class WebView2Callbacks
{
    // Layout offsets (in IntPtr-sized slots) on the object block.
    private const int Slot_Vtable = 0;
    private const int Slot_Refcount = 1;
    private const int Slot_UserData = 2;  // delegate* or arbitrary IntPtr the callback wants
    private const int ObjectSlotCount = 3;

    /// <summary>
    /// Build a 4-slot vtable: QI, AddRef, Release, Invoke. The Invoke slot
    /// is supplied per call.
    /// </summary>
    private static IntPtr* AllocVtable4(IntPtr invokePtr,
        delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> qi)
    {
        var vt = (IntPtr*)NativeMemory.Alloc(4 * (nuint)sizeof(IntPtr));
        vt[0] = (IntPtr)qi;
        vt[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefStub;
        vt[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseStub;
        vt[3] = invokePtr;
        return vt;
    }

    private static IntPtr AllocObject(IntPtr vtable)
    {
        var obj = (IntPtr*)NativeMemory.AllocZeroed((nuint)ObjectSlotCount * (nuint)sizeof(IntPtr));
        obj[Slot_Vtable] = vtable;
        obj[Slot_Refcount] = (IntPtr)1;
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
        var v = (uint)Interlocked.Decrement(ref *refSlot);
        if (v == 0)
        {
            // Free both the vtable and the object block. The vtable was
            // allocated per-instance so we can free it.
            var vt = ((IntPtr*)self)[Slot_Vtable];
            NativeMemory.Free((void*)vt);
            NativeMemory.Free((void*)self);
        }
        return v;
    }

    // ============== Env-created handler ==============

    public static IntPtr CreateEnvCreatedHandler(
        delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &EnvQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int EnvQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    // ============== Controller-created handler ==============

    public static IntPtr CreateControllerCreatedHandler(
        delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &CtrlQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int CtrlQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2CreateCoreWebView2ControllerCompletedHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    // ============== WebMessageReceived event handler ==============

    public static IntPtr CreateWebMessageReceivedHandler(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &WmrQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int WmrQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2WebMessageReceivedEventHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    // ============== NavigationCompleted event handler ==============

    public static IntPtr CreateNavigationCompletedHandler(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &NavQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int NavQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2NavigationCompletedEventHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    // ============== NavigationStarting event handler ==============

    public static IntPtr CreateNavigationStartingHandler(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &NavStartingQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int NavStartingQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2NavigationStartingEventHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    // ============== NewWindowRequested event handler ==============

    public static IntPtr CreateNewWindowRequestedHandler(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &NewWindowQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int NewWindowQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2NewWindowRequestedEventHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }

    // ============== PermissionRequested event handler ==============

    public static IntPtr CreatePermissionRequestedHandler(
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int> onInvoke)
    {
        var vt = AllocVtable4((IntPtr)onInvoke, &PermissionQI);
        return AllocObject((IntPtr)vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int PermissionQI(IntPtr self, Guid* iid, IntPtr* ppv)
    {
        if (iid == null) return WebView2Native.E_POINTER;
        if (*iid == WebView2Native.IID_IUnknown ||
            *iid == WebView2Native.IID_ICoreWebView2PermissionRequestedEventHandler)
        {
            *ppv = self;
            AddRefInternal(self);
            return WebView2Native.S_OK;
        }
        *ppv = IntPtr.Zero;
        return WebView2Native.E_NOINTERFACE;
    }
}
