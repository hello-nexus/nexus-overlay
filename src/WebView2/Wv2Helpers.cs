using System;
using System.Runtime.InteropServices;

namespace Qos.Overlay.WebView2;

/// <summary>
/// Thin helpers for invoking COM vtable methods on raw IntPtr COM objects.
/// All methods preserve and convert HRESULTs; callers check via
/// <c>WebView2Native.Succeeded</c>.
/// </summary>
internal static unsafe class Wv2
{
    /// <summary>Reads slot <paramref name="slot"/> from the vtable of a COM object.</summary>
    public static IntPtr Slot(IntPtr comObject, int slot)
    {
        var vt = *(IntPtr**)comObject;
        return vt[slot];
    }

    public static uint AddRef(IntPtr comObject)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, WebView2Vtable.IUnknown_AddRef);
        return fn(comObject);
    }

    public static uint Release(IntPtr comObject)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(comObject, WebView2Vtable.IUnknown_Release);
        return fn(comObject);
    }

    /// <summary>QueryInterface for an additional interface (e.g. ICoreWebView2Controller2). Returns null on E_NOINTERFACE.</summary>
    public static IntPtr QueryInterface(IntPtr comObject, in Guid iid)
    {
        IntPtr result;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(comObject, WebView2Vtable.IUnknown_QueryInterface);
        fixed (Guid* p = &iid)
        {
            var hr = fn(comObject, p, &result);
            if (WebView2Native.Failed(hr)) return IntPtr.Zero;
        }
        return result;
    }

    // ===================== ICoreWebView2Environment =====================

    public static int Env_CreateCoreWebView2Controller(IntPtr env, IntPtr parentHwnd, IntPtr handler)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Slot(env, WebView2Vtable.Env_CreateCoreWebView2Controller);
        return fn(env, parentHwnd, handler);
    }

    // ===================== ICoreWebView2Controller =====================

    public static int Ctrl_put_IsVisible(IntPtr ctrl, bool visible)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(ctrl, WebView2Vtable.Ctrl_put_IsVisible);
        return fn(ctrl, visible ? 1 : 0);
    }

    public static int Ctrl_put_Bounds(IntPtr ctrl, Win32.Native.RECT bounds)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Win32.Native.RECT, int>)Slot(ctrl, WebView2Vtable.Ctrl_put_Bounds);
        return fn(ctrl, bounds);
    }

    public static int Ctrl_get_CoreWebView2(IntPtr ctrl, out IntPtr core)
    {
        IntPtr c;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(ctrl, WebView2Vtable.Ctrl_get_CoreWebView2);
        var hr = fn(ctrl, &c);
        core = c;
        return hr;
    }

    public static int Ctrl_Close(IntPtr ctrl)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(ctrl, WebView2Vtable.Ctrl_Close);
        return fn(ctrl);
    }

    public static readonly Guid IID_ICoreWebView2Controller2 =
        new("c979903e-d4ca-4228-92eb-47ee3fa96eab");

    public static int Ctrl2_put_DefaultBackgroundColor(IntPtr ctrl2, uint argb)
    {
        // ICoreWebView2Controller2 has its own vtable, but slot indices for
        // inherited methods are duplicated. The 'put_DefaultBackgroundColor'
        // is at slot 27 (after the 25 Controller methods + get/put pair) -
        // re-check if SDK version changes.
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(ctrl2, WebView2Vtable.Ctrl2_put_DefaultBackgroundColor);
        return fn(ctrl2, argb);
    }

    // ===================== ICoreWebView2 =====================

    public static int Wv2_Navigate(IntPtr wv2, string url)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(wv2, WebView2Vtable.Wv2_Navigate);
        fixed (char* u = url) return fn(wv2, u);
    }

    public static int Wv2_get_Settings(IntPtr wv2, out IntPtr settings)
    {
        IntPtr s;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(wv2, WebView2Vtable.Wv2_get_Settings);
        var hr = fn(wv2, &s);
        settings = s;
        return hr;
    }

    public static int Wv2_add_WebMessageReceived(IntPtr wv2, IntPtr handler, out long token)
    {
        long t;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(wv2, WebView2Vtable.Wv2_add_WebMessageReceived);
        var hr = fn(wv2, handler, &t);
        token = t;
        return hr;
    }

    public static int Wv2_add_NavigationCompleted(IntPtr wv2, IntPtr handler, out long token)
    {
        long t;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(wv2, WebView2Vtable.Wv2_add_NavigationCompleted);
        var hr = fn(wv2, handler, &t);
        token = t;
        return hr;
    }

    // ===================== ICoreWebView2Settings =====================

    public static int Settings_put_AreDefaultContextMenusEnabled(IntPtr s, bool v) =>
        SettingsPutBool(s, WebView2Vtable.Settings_put_AreDefaultContextMenusEnabled, v);

    public static int Settings_put_AreDevToolsEnabled(IntPtr s, bool v) =>
        SettingsPutBool(s, WebView2Vtable.Settings_put_AreDevToolsEnabled, v);

    public static int Settings_put_IsStatusBarEnabled(IntPtr s, bool v) =>
        SettingsPutBool(s, WebView2Vtable.Settings_put_IsStatusBarEnabled, v);

    public static int Settings_put_IsZoomControlEnabled(IntPtr s, bool v) =>
        SettingsPutBool(s, WebView2Vtable.Settings_put_IsZoomControlEnabled, v);

    private static int SettingsPutBool(IntPtr settings, int slot, bool v)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(settings, slot);
        return fn(settings, v ? 1 : 0);
    }

    // ===================== Event args =====================

    public static int WebMsgArgs_get_WebMessageAsJson(IntPtr args, out IntPtr cotaskMemStr)
    {
        IntPtr p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, WebView2Vtable.WebMsgArgs_get_WebMessageAsJson);
        var hr = fn(args, &p);
        cotaskMemStr = p;
        return hr;
    }

    public static int NavCompletedArgs_get_IsSuccess(IntPtr args, out bool isSuccess)
    {
        int s;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, WebView2Vtable.NavCompletedArgs_get_IsSuccess);
        var hr = fn(args, &s);
        isSuccess = s != 0;
        return hr;
    }

    public static int NavCompletedArgs_get_WebErrorStatus(IntPtr args, out int status)
    {
        int s;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, WebView2Vtable.NavCompletedArgs_get_WebErrorStatus);
        var hr = fn(args, &s);
        status = s;
        return hr;
    }
}
