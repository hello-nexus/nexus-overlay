namespace Qos.Overlay.WebView2;

/// <summary>
/// COM vtable slot indices for every WebView2 interface this overlay
/// consumes. Slots 0/1/2 are always IUnknown (QueryInterface, AddRef,
/// Release); own methods start at 3. The numbers are taken from
/// WebView2.h in the Microsoft.Web.WebView2 1.0.2792.45 package - bump
/// the package version in lockstep with this table.
/// </summary>
internal static class WebView2Vtable
{
    // IUnknown (shared by every COM interface).
    public const int IUnknown_QueryInterface = 0;
    public const int IUnknown_AddRef = 1;
    public const int IUnknown_Release = 2;

    // ICoreWebView2Environment (5 own methods, last has the inner CoreWebView2 getter).
    public const int Env_CreateCoreWebView2Controller = 3;
    public const int Env_CreateWebResourceResponse = 4;
    public const int Env_get_BrowserVersionString = 5;
    public const int Env_add_NewBrowserVersionAvailable = 6;
    public const int Env_remove_NewBrowserVersionAvailable = 7;

    // ICoreWebView2Controller (own methods 3..25; only the ones we use are named).
    public const int Ctrl_get_IsVisible = 3;
    public const int Ctrl_put_IsVisible = 4;
    public const int Ctrl_get_Bounds = 5;
    public const int Ctrl_put_Bounds = 6;
    // 7..20 skipped (zoom, focus, accelerators)
    public const int Ctrl_get_ParentWindow = 21;
    public const int Ctrl_put_ParentWindow = 22;
    public const int Ctrl_NotifyParentWindowPositionChanged = 23;
    public const int Ctrl_Close = 24;
    public const int Ctrl_get_CoreWebView2 = 25;

    // ICoreWebView2Controller2 (extends Controller, adds DefaultBackgroundColor).
    // QI separately for IID_ICoreWebView2Controller2 to access these.
    public const int Ctrl2_get_DefaultBackgroundColor = 26;
    public const int Ctrl2_put_DefaultBackgroundColor = 27;

    // ICoreWebView2 (own methods 3..). Subset:
    public const int Wv2_get_Settings = 3;
    public const int Wv2_get_Source = 4;
    public const int Wv2_Navigate = 5;
    public const int Wv2_NavigateToString = 6;
    public const int Wv2_add_NavigationStarting = 7;
    public const int Wv2_remove_NavigationStarting = 8;
    public const int Wv2_add_ContentLoading = 9;
    public const int Wv2_remove_ContentLoading = 10;
    public const int Wv2_add_SourceChanged = 11;
    public const int Wv2_remove_SourceChanged = 12;
    public const int Wv2_add_HistoryChanged = 13;
    public const int Wv2_remove_HistoryChanged = 14;
    public const int Wv2_add_NavigationCompleted = 15;
    public const int Wv2_remove_NavigationCompleted = 16;
    public const int Wv2_add_FrameNavigationStarting = 17;
    public const int Wv2_remove_FrameNavigationStarting = 18;
    public const int Wv2_add_FrameNavigationCompleted = 19;
    public const int Wv2_remove_FrameNavigationCompleted = 20;
    public const int Wv2_add_ScriptDialogOpening = 21;
    public const int Wv2_remove_ScriptDialogOpening = 22;
    public const int Wv2_add_PermissionRequested = 23;
    public const int Wv2_remove_PermissionRequested = 24;
    public const int Wv2_add_ProcessFailed = 25;
    public const int Wv2_remove_ProcessFailed = 26;
    public const int Wv2_AddScriptToExecuteOnDocumentCreated = 27;
    public const int Wv2_RemoveScriptToExecuteOnDocumentCreated = 28;
    public const int Wv2_ExecuteScript = 29;
    public const int Wv2_CapturePreview = 30;
    public const int Wv2_Reload = 31;
    public const int Wv2_PostWebMessageAsJson = 32;
    public const int Wv2_PostWebMessageAsString = 33;
    public const int Wv2_add_WebMessageReceived = 34;
    public const int Wv2_remove_WebMessageReceived = 35;

    // ICoreWebView2Settings (own methods 3..). Subset we use:
    public const int Settings_get_IsScriptEnabled = 3;
    public const int Settings_put_IsScriptEnabled = 4;
    public const int Settings_get_IsWebMessageEnabled = 5;
    public const int Settings_put_IsWebMessageEnabled = 6;
    public const int Settings_get_AreDefaultScriptDialogsEnabled = 7;
    public const int Settings_put_AreDefaultScriptDialogsEnabled = 8;
    public const int Settings_get_IsStatusBarEnabled = 9;
    public const int Settings_put_IsStatusBarEnabled = 10;
    public const int Settings_get_AreDevToolsEnabled = 11;
    public const int Settings_put_AreDevToolsEnabled = 12;
    public const int Settings_get_AreDefaultContextMenusEnabled = 13;
    public const int Settings_put_AreDefaultContextMenusEnabled = 14;
    public const int Settings_get_AreHostObjectsAllowed = 15;
    public const int Settings_put_AreHostObjectsAllowed = 16;
    public const int Settings_get_IsZoomControlEnabled = 17;
    public const int Settings_put_IsZoomControlEnabled = 18;
    public const int Settings_get_IsBuiltInErrorPageEnabled = 19;
    public const int Settings_put_IsBuiltInErrorPageEnabled = 20;

    // ICoreWebView2Settings3 (extends Settings2). AreBrowserAcceleratorKeysEnabled
    // is on ICoreWebView2Settings3 specifically. QI separately for that IID.
    // For simplicity we'll skip put_AreBrowserAcceleratorKeysEnabled in the
    // first pass and assert the default behavior is acceptable.

    // ICoreWebView2WebMessageReceivedEventArgs (own methods 3..).
    public const int WebMsgArgs_get_Source = 3;
    public const int WebMsgArgs_get_WebMessageAsJson = 4;
    public const int WebMsgArgs_TryGetWebMessageAsString = 5;

    // ICoreWebView2NavigationCompletedEventArgs (own methods 3..).
    public const int NavCompletedArgs_get_IsSuccess = 3;
    public const int NavCompletedArgs_get_WebErrorStatus = 4;
    public const int NavCompletedArgs_get_NavigationId = 5;
}
