namespace Nexus.Overlay.WebView2;

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

    // ICoreWebView2Controller3 (extends Controller2, adds RasterizationScale /
    // ShouldDetectMonitorScaleChanges / BoundsMode). Slots continue after
    // Controller2's 26/27. QI separately for IID_ICoreWebView2Controller3.
    public const int Ctrl3_get_RasterizationScale = 28;
    public const int Ctrl3_put_RasterizationScale = 29;
    public const int Ctrl3_get_ShouldDetectMonitorScaleChanges = 30;
    public const int Ctrl3_put_ShouldDetectMonitorScaleChanges = 31;

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
    public const int ProcessFailedArgs_get_ProcessFailedKind = 3;
    public const int Wv2_AddScriptToExecuteOnDocumentCreated = 27;
    public const int Wv2_RemoveScriptToExecuteOnDocumentCreated = 28;
    public const int Wv2_ExecuteScript = 29;
    public const int Wv2_CapturePreview = 30;
    public const int Wv2_Reload = 31;
    public const int Wv2_PostWebMessageAsJson = 32;
    public const int Wv2_PostWebMessageAsString = 33;
    public const int Wv2_add_WebMessageReceived = 34;
    public const int Wv2_remove_WebMessageReceived = 35;
    // 36..43 skipped (CallDevToolsProtocolMethod, BrowserProcessId, history, etc.)
    public const int Wv2_add_NewWindowRequested = 44;
    public const int Wv2_remove_NewWindowRequested = 45;

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
    // is on ICoreWebView2Settings3 specifically; QI separately for that IID.
    // Not wired up.

    // ICoreWebView2Settings4 (extends Settings3). Adds IsPasswordAutosaveEnabled
    // and IsGeneralAutofillEnabled. QI separately for IID_ICoreWebView2Settings4.
    public const int Settings4_get_IsPasswordAutosaveEnabled = 25;
    public const int Settings4_put_IsPasswordAutosaveEnabled = 26;
    public const int Settings4_get_IsGeneralAutofillEnabled = 27;
    public const int Settings4_put_IsGeneralAutofillEnabled = 28;

    // ICoreWebView2Settings9 (extends Settings8). Slots are 0-based from the
    // first IUnknown method; Settings adds 2 methods per property bumped
    // through 8 prior versions. IsNonClientRegionSupportEnabled is added at
    // the tail. QI separately for IID_ICoreWebView2Settings9.
    //   Settings:  3..20  (9 props)
    //   Settings2: 21..22 (UserAgent)
    //   Settings3: 23..24 (AreBrowserAcceleratorKeysEnabled)
    //   Settings4: 25..28 (IsPasswordAutosaveEnabled, IsGeneralAutofillEnabled)
    //   Settings5: 29..30 (IsPinchZoomEnabled)
    //   Settings6: 31..32 (IsSwipeNavigationEnabled)
    //   Settings7: 33..34 (HiddenPdfToolbarItems)
    //   Settings8: 35..36 (IsReputationCheckingRequired)
    //   Settings9: 37..38 (IsNonClientRegionSupportEnabled)
    public const int Settings9_get_IsNonClientRegionSupportEnabled = 37;
    public const int Settings9_put_IsNonClientRegionSupportEnabled = 38;

    // ICoreWebView2WebMessageReceivedEventArgs (own methods 3..).
    public const int WebMsgArgs_get_Source = 3;
    public const int WebMsgArgs_get_WebMessageAsJson = 4;
    public const int WebMsgArgs_TryGetWebMessageAsString = 5;

    // ICoreWebView2WebMessageReceivedEventArgs2 (derives from the above).
    public const int WebMsgArgs2_get_AdditionalObjects = 6;

    // ICoreWebView2ObjectCollectionView (own methods 3..).
    public const int ObjectCollection_get_Count = 3;
    public const int ObjectCollection_GetValueAtIndex = 4;

    // ICoreWebView2File (own methods 3..).
    public const int File_get_Path = 3;

    // ICoreWebView2NavigationCompletedEventArgs (own methods 3..).
    public const int NavCompletedArgs_get_IsSuccess = 3;
    public const int NavCompletedArgs_get_WebErrorStatus = 4;
    public const int NavCompletedArgs_get_NavigationId = 5;

    // ICoreWebView2NavigationStartingEventArgs (own methods 3..).
    public const int NavStartingArgs_get_Uri = 3;
    public const int NavStartingArgs_get_IsUserInitiated = 4;
    public const int NavStartingArgs_get_IsRedirected = 5;
    public const int NavStartingArgs_get_RequestHeaders = 6;
    public const int NavStartingArgs_get_Cancel = 7;
    public const int NavStartingArgs_put_Cancel = 8;
    public const int NavStartingArgs_get_NavigationId = 9;

    // ICoreWebView2NewWindowRequestedEventArgs (own methods 3..).
    public const int NewWindowArgs_get_Uri = 3;
    public const int NewWindowArgs_put_NewWindow = 4;
    public const int NewWindowArgs_get_NewWindow = 5;
    public const int NewWindowArgs_put_Handled = 6;
    public const int NewWindowArgs_get_Handled = 7;
    public const int NewWindowArgs_get_IsUserInitiated = 8;
    public const int NewWindowArgs_GetDeferral = 9;
    public const int NewWindowArgs_get_WindowFeatures = 10;

    // ICoreWebView2PermissionRequestedEventArgs (own methods 3..).
    public const int PermissionArgs_get_Uri = 3;
    public const int PermissionArgs_get_PermissionKind = 4;
    public const int PermissionArgs_get_IsUserInitiated = 5;
    public const int PermissionArgs_get_State = 6;
    public const int PermissionArgs_put_State = 7;
    public const int PermissionArgs_GetDeferral = 8;
}
