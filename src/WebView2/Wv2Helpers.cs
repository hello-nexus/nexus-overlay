using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.WebView2;

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

    // ===================== ICoreWebView2Controller3 =====================

    public static readonly Guid IID_ICoreWebView2Controller3 =
        new("f9614724-5d2b-41dc-aef7-73d62b51543b");

    public static int Ctrl3_put_RasterizationScale(IntPtr ctrl3, double scale)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, double, int>)Slot(ctrl3, WebView2Vtable.Ctrl3_put_RasterizationScale);
        return fn(ctrl3, scale);
    }

    public static int Ctrl3_put_ShouldDetectMonitorScaleChanges(IntPtr ctrl3, bool value)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(ctrl3, WebView2Vtable.Ctrl3_put_ShouldDetectMonitorScaleChanges);
        return fn(ctrl3, value ? 1 : 0);
    }

    // ===================== ICoreWebView2 =====================

    public static int Wv2_Navigate(IntPtr wv2, string url)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(wv2, WebView2Vtable.Wv2_Navigate);
        fixed (char* u = url) return fn(wv2, u);
    }

    public static int Wv2_PostWebMessageAsJson(IntPtr wv2, string json)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(wv2, WebView2Vtable.Wv2_PostWebMessageAsJson);
        fixed (char* j = json) return fn(wv2, j);
    }

    public static int Wv2_ExecuteScript(IntPtr wv2, string script, IntPtr completedHandler)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int>)Slot(wv2, WebView2Vtable.Wv2_ExecuteScript);
        fixed (char* s = script) return fn(wv2, s, completedHandler);
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

    public static int Wv2_remove_NavigationCompleted(IntPtr wv2, long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(wv2, WebView2Vtable.Wv2_remove_NavigationCompleted);
        return fn(wv2, token);
    }

    public static int Wv2_add_NavigationStarting(IntPtr wv2, IntPtr handler, out long token)
    {
        long t;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(wv2, WebView2Vtable.Wv2_add_NavigationStarting);
        var hr = fn(wv2, handler, &t);
        token = t;
        return hr;
    }

    public static int Wv2_add_NewWindowRequested(IntPtr wv2, IntPtr handler, out long token)
    {
        long t;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(wv2, WebView2Vtable.Wv2_add_NewWindowRequested);
        var hr = fn(wv2, handler, &t);
        token = t;
        return hr;
    }

    public static int Wv2_add_PermissionRequested(IntPtr wv2, IntPtr handler, out long token)
    {
        long t;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(wv2, WebView2Vtable.Wv2_add_PermissionRequested);
        var hr = fn(wv2, handler, &t);
        token = t;
        return hr;
    }

    public static int Wv2_remove_NavigationStarting(IntPtr wv2, long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(wv2, WebView2Vtable.Wv2_remove_NavigationStarting);
        return fn(wv2, token);
    }

    public static int Wv2_remove_NewWindowRequested(IntPtr wv2, long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(wv2, WebView2Vtable.Wv2_remove_NewWindowRequested);
        return fn(wv2, token);
    }

    public static int Wv2_remove_PermissionRequested(IntPtr wv2, long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(wv2, WebView2Vtable.Wv2_remove_PermissionRequested);
        return fn(wv2, token);
    }

    public static int Wv2_remove_WebMessageReceived(IntPtr wv2, long token)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(wv2, WebView2Vtable.Wv2_remove_WebMessageReceived);
        return fn(wv2, token);
    }

    /// <summary>
    /// Queues a script to run on document creation in every navigation. We
    /// drop the completion handler (pass IntPtr.Zero) - we don't need the
    /// returned script ID since we never remove what we add. AOT-safe: the
    /// script string is fixed in a pinned buffer for the call duration.
    /// </summary>
    public static int Wv2_AddScriptToExecuteOnDocumentCreated(IntPtr wv2, string script)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr, int>)Slot(wv2, WebView2Vtable.Wv2_AddScriptToExecuteOnDocumentCreated);
        fixed (char* s = script) return fn(wv2, s, IntPtr.Zero);
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

    // ===================== ICoreWebView2Settings4 =====================

    public static int Settings4_put_IsPasswordAutosaveEnabled(IntPtr settings4, bool v) =>
        SettingsPutBool(settings4, WebView2Vtable.Settings4_put_IsPasswordAutosaveEnabled, v);

    public static int Settings4_put_IsGeneralAutofillEnabled(IntPtr settings4, bool v) =>
        SettingsPutBool(settings4, WebView2Vtable.Settings4_put_IsGeneralAutofillEnabled, v);

    // ===================== ICoreWebView2Settings9 =====================

    public static int Settings9_put_IsNonClientRegionSupportEnabled(IntPtr settings9, bool v) =>
        SettingsPutBool(settings9, WebView2Vtable.Settings9_put_IsNonClientRegionSupportEnabled, v);

    // ===================== Event args =====================

    public static int WebMsgArgs_get_WebMessageAsJson(IntPtr args, out IntPtr cotaskMemStr)
    {
        IntPtr p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, WebView2Vtable.WebMsgArgs_get_WebMessageAsJson);
        var hr = fn(args, &p);
        cotaskMemStr = p;
        return hr;
    }

    public static int WebMsgArgs_TryGetWebMessageAsString(IntPtr args, out string? text)
    {
        IntPtr p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, WebView2Vtable.WebMsgArgs_TryGetWebMessageAsString);
        var hr = fn(args, &p);
        if (WebView2Native.Failed(hr) || p == IntPtr.Zero) { text = null; return hr; }
        text = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return hr;
    }

    /// <summary>
    /// Disk paths of the File objects a page passed via
    /// chrome.webview.postMessageWithAdditionalObjects. Empty when the
    /// runtime predates args2 (SDK &lt; 1.0.1518) or nothing was attached;
    /// non-File entries are skipped.
    /// </summary>
    public static List<string> WebMsgArgs_GetAdditionalFilePaths(IntPtr args)
    {
        var paths = new List<string>();
        var args2 = QueryInterface(args, WebView2Native.IID_ICoreWebView2WebMessageReceivedEventArgs2);
        if (args2 == IntPtr.Zero) return paths;
        try
        {
            IntPtr collection;
            var getFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args2, WebView2Vtable.WebMsgArgs2_get_AdditionalObjects);
            if (WebView2Native.Failed(getFn(args2, &collection)) || collection == IntPtr.Zero) return paths;
            try
            {
                uint count;
                var countFn = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(collection, WebView2Vtable.ObjectCollection_get_Count);
                if (WebView2Native.Failed(countFn(collection, &count))) return paths;

                var atFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(collection, WebView2Vtable.ObjectCollection_GetValueAtIndex);
                for (uint i = 0; i < count; i++)
                {
                    IntPtr obj;
                    if (WebView2Native.Failed(atFn(collection, i, &obj)) || obj == IntPtr.Zero) continue;
                    try
                    {
                        var file = QueryInterface(obj, WebView2Native.IID_ICoreWebView2File);
                        if (file == IntPtr.Zero) continue;
                        try
                        {
                            IntPtr pathPtr;
                            var pathFn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(file, WebView2Vtable.File_get_Path);
                            if (WebView2Native.Failed(pathFn(file, &pathPtr)) || pathPtr == IntPtr.Zero) continue;
                            var path = Marshal.PtrToStringUni(pathPtr);
                            Marshal.FreeCoTaskMem(pathPtr);
                            if (!string.IsNullOrEmpty(path)) paths.Add(path);
                        }
                        finally { Release(file); }
                    }
                    finally { Release(obj); }
                }
            }
            finally { Release(collection); }
        }
        finally { Release(args2); }
        return paths;
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

    // ===================== NavigationStarting args =====================

    public static int NavStartingArgs_get_Uri(IntPtr args, out string? uri)
    {
        IntPtr p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, WebView2Vtable.NavStartingArgs_get_Uri);
        var hr = fn(args, &p);
        if (WebView2Native.Failed(hr) || p == IntPtr.Zero) { uri = null; return hr; }
        uri = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return hr;
    }

    public static int NavStartingArgs_put_Cancel(IntPtr args, bool cancel)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(args, WebView2Vtable.NavStartingArgs_put_Cancel);
        return fn(args, cancel ? 1 : 0);
    }

    // ===================== NewWindowRequested args =====================

    public static int NewWindowArgs_get_Uri(IntPtr args, out string? uri)
    {
        IntPtr p;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(args, WebView2Vtable.NewWindowArgs_get_Uri);
        var hr = fn(args, &p);
        if (WebView2Native.Failed(hr) || p == IntPtr.Zero) { uri = null; return hr; }
        uri = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return hr;
    }

    public static int NewWindowArgs_put_Handled(IntPtr args, bool handled)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(args, WebView2Vtable.NewWindowArgs_put_Handled);
        return fn(args, handled ? 1 : 0);
    }

    // ===================== PermissionRequested args =====================

    public static int PermissionArgs_get_PermissionKind(IntPtr args, out int kind)
    {
        int k;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, WebView2Vtable.PermissionArgs_get_PermissionKind);
        var hr = fn(args, &k);
        kind = k;
        return hr;
    }

    public static int PermissionArgs_put_State(IntPtr args, int state)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(args, WebView2Vtable.PermissionArgs_put_State);
        return fn(args, state);
    }
}
