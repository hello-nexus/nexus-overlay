using System;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.WebView2;

/// <summary>
/// Single export from <c>WebView2Loader.dll</c> we consume. Everything else
/// is reached through COM vtables on the returned interface pointers.
/// </summary>
internal static unsafe class WebView2Native
{
    [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
    public static extern int CreateCoreWebView2EnvironmentWithOptions(
        char* browserExecutableFolder,
        char* userDataFolder,
        IntPtr environmentOptions,
        IntPtr environmentCreatedHandler);

    // ===================== IIDs =====================
    // From WebView2.idl. Kept centralized so the slot map in WebView2Vtable
    // and the GUIDs here move in lockstep when we bump SDK versions.

    public static readonly Guid IID_IUnknown =
        new("00000000-0000-0000-C000-000000000046");

    public static readonly Guid IID_ICoreWebView2EnvironmentOptions =
        new("2fde08a8-1e9a-4766-8c05-95a9ceb9d1c5");

    public static readonly Guid IID_ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler =
        new("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d");

    public static readonly Guid IID_ICoreWebView2Environment =
        new("b96d755e-0319-4e92-a296-23436f46a1fc");

    public static readonly Guid IID_ICoreWebView2CreateCoreWebView2ControllerCompletedHandler =
        new("6c4819f3-c9b7-4260-8127-c9f5bde7f68c");

    public static readonly Guid IID_ICoreWebView2Controller =
        new("4d00c0d1-9434-4eb6-8078-8697a560334f");

    public static readonly Guid IID_ICoreWebView2 =
        new("76eceacb-0462-4d94-ac83-423a6793775e");

    public static readonly Guid IID_ICoreWebView2Settings =
        new("e562e4f0-d7fa-43ac-8d71-c05150499f00");

    // ICoreWebView2Settings9 (extends Settings8). Adds
    // IsNonClientRegionSupportEnabled: CSS `app-region: drag` regions in the
    // page forward mouse events to the host's WM_NCHITTEST, enabling a custom
    // title bar (no system caption, draggable top strip + DWM-painted
    // min/max/close) inside a WebView2 child window.
    public static readonly Guid IID_ICoreWebView2Settings9 =
        new("0528a73b-e92d-49f4-927a-e547dddaa37d");

    public static readonly Guid IID_ICoreWebView2WebMessageReceivedEventHandler =
        new("57213f19-00e6-49fa-8e07-898ea01ecbd2");

    public static readonly Guid IID_ICoreWebView2WebMessageReceivedEventArgs =
        new("0f99a40c-e962-4207-9e92-e3d542eff849");

    // postMessageWithAdditionalObjects support (SDK 1.0.1518.46+): args2
    // exposes the passed DOM objects; File entries surface their real disk
    // path — the only way a web drop can become a path reference.
    public static readonly Guid IID_ICoreWebView2WebMessageReceivedEventArgs2 =
        new("06fc7ab7-c90c-4297-9389-33ca01cf6d5e");

    public static readonly Guid IID_ICoreWebView2ObjectCollectionView =
        new("0f36fd87-4f69-4415-98da-888f89fb9a33");

    public static readonly Guid IID_ICoreWebView2File =
        new("f2c19559-6bc1-4583-a757-90021be9afec");

    public static readonly Guid IID_ICoreWebView2NavigationCompletedEventHandler =
        new("d33a35bf-1c49-4f98-93ab-006e0533fe1c");

    public static readonly Guid IID_ICoreWebView2NavigationCompletedEventArgs =
        new("30d68b7d-20d9-4752-a9ca-ec8448fbb5c1");

    public static readonly Guid IID_ICoreWebView2NavigationStartingEventHandler =
        new("9adbe429-f36d-432b-9ddc-f8881fbd76e3");

    public static readonly Guid IID_ICoreWebView2NavigationStartingEventArgs =
        new("5b495469-e119-438a-9b18-7604f25f2e49");

    public static readonly Guid IID_ICoreWebView2NewWindowRequestedEventHandler =
        new("d4c185fe-c81c-4989-97af-2d3fa7ab5651");

    public static readonly Guid IID_ICoreWebView2NewWindowRequestedEventArgs =
        new("34acb11c-fc37-4418-9132-f9c21d1eafb9");

    public static readonly Guid IID_ICoreWebView2PermissionRequestedEventHandler =
        new("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd");

    public static readonly Guid IID_ICoreWebView2PermissionRequestedEventArgs =
        new("973ae2ef-ff18-4894-8fb2-3c758f046810");

    // ===================== HRESULTs =====================

    public const int S_OK = 0;
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);

    public static bool Failed(int hr) => hr < 0;
    public static bool Succeeded(int hr) => hr >= 0;
}
