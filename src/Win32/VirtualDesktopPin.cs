using System;
using System.Runtime.InteropServices;

namespace Nexus.Overlay.Win32;

/// <summary>
/// Pins the overlay's app identity across every Windows virtual desktop
/// via <c>IVirtualDesktopPinnedApps::PinAppID</c>. The dance is:
///
/// 1. Set an explicit AppUserModelID on the process at startup
///    (<c>SetCurrentProcessExplicitAppUserModelID</c>).
/// 2. <c>CoCreateInstance(CLSID_ImmersiveShell, IID_IServiceProvider)</c>.
/// 3. <c>QueryService(SID_VirtualDesktopPinnedApps,
///    IID_IVirtualDesktopPinnedApps)</c>.
/// 4. <c>IVirtualDesktopPinnedApps::PinAppID(L"Nexus.Overlay")</c>.
///
/// Why AppID, not PinView/PinWindow:
/// - <c>PinView</c> requires an <c>IApplicationView</c>, which the shell
///   does NOT create for <c>WS_EX_TOOLWINDOW</c> HWNDs (our overlay is
///   one) - GetViewForHwnd returns TYPE_E_ELEMENTNOTFOUND (0x8002802B).
/// - <c>PinWindow(HWND)</c> lives on <c>IVirtualDesktopManagerInternal</c>,
///   whose IID changes across Windows feature updates - fragile.
/// - <c>PinAppID(LPCWSTR)</c> is on the same <c>IVirtualDesktopPinnedApps</c>
///   interface whose IID has been stable for years and works regardless
///   of the window's shell visibility.
///
/// The shell stops hiding the HWND when switching desktops.
/// </summary>
internal static unsafe class VirtualDesktopPin
{
    public const string OverlayAppUserModelId = "Nexus.Overlay";

    private static readonly Guid CLSID_ImmersiveShell =
        new("C2F03A33-21F5-47FA-B4BB-156362A2F239");

    private static readonly Guid IID_IServiceProvider =
        new("6D5140C1-7436-11CE-8034-00AA006009FA");

    // Win11 25H2 doesn't expose IVirtualDesktopPinnedApps directly via
    // QueryService (returns E_NOTIMPL). Have to go through
    // IVirtualDesktopManagerInternal and QI from there.
    private static readonly Guid SID_VirtualDesktopManagerInternal =
        new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    // IID for IVirtualDesktopManagerInternal changes across Windows builds.
    // Try them in order; any object that QIs to IVirtualDesktopPinnedApps
    // suffices (no methods on this interface are invoked).
    private static readonly Guid[] IID_IVirtualDesktopManagerInternal_Candidates = new[]
    {
        // Win11 24H2 / 25H2:
        new Guid("53F5CA0B-158F-4124-900C-057E60B1A2BE"),
        // Win11 22H2 / 23H2:
        new Guid("4970BA3D-FD4E-4647-BEA3-D89076EF4B9C"),
        // Win11 21H2:
        new Guid("B2F925B9-5A0F-4D2E-9F4D-2B1507593C10"),
        // Win10 1809+:
        new Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"),
    };

    private static readonly Guid SID_VirtualDesktopPinnedApps =
        new("4CE81583-1E4C-4632-A621-07A53543148F");

    private static readonly Guid IID_IVirtualDesktopPinnedApps =
        new("4CE81583-1E4C-4632-A621-07A53543148F");

    // Vtable slot indices (after IUnknown's 3 slots).
    private const int IServiceProvider_QueryService = 3;
    private const int IVirtualDesktopPinnedApps_IsAppIdPinned = 3;
    private const int IVirtualDesktopPinnedApps_PinAppID = 4;

    private const uint CLSCTX_LOCAL_SERVER = 0x4;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        in Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    /// <summary>
    /// Set the process's AppUserModelID. Must be called BEFORE creating
    /// any window we want pinned - the system associates each new HWND
    /// with the AUMID active at creation time.
    /// </summary>
    public static void SetProcessAppId()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(OverlayAppUserModelId);
            Log.Info($"pin: set AUMID={OverlayAppUserModelId}");
        }
        catch (Exception ex)
        {
            Log.Warn($"pin: SetAUMID failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Pin a specific HWND across all virtual desktops via
    /// <c>IVirtualDesktopManagerInternal::PinWindow</c>. Disabled: the
    /// PinWindow slot index shifts across Windows builds and calling the
    /// wrong slot (different signature) crashes the process, so this returns
    /// false until a reliable per-build slot map exists.
    /// </summary>
    public static bool TryPinWindow(IntPtr hwnd)
    {
        return false;
    }

    /// <summary>
    /// Pin the overlay's AppID across all virtual desktops. Returns true
    /// on success; failures are logged and silently absorbed (the overlay
    /// still works on the current desktop, just not cross-desktop).
    /// </summary>
    public static bool TryPinApp()
    {
        IntPtr serviceProvider = IntPtr.Zero;
        IntPtr managerInternal = IntPtr.Zero;
        IntPtr pinnedApps = IntPtr.Zero;
        IntPtr appIdPtr = IntPtr.Zero;

        try
        {
            var hr = CoCreateInstance(CLSID_ImmersiveShell, IntPtr.Zero, CLSCTX_LOCAL_SERVER,
                IID_IServiceProvider, out serviceProvider);
            if (hr < 0 || serviceProvider == IntPtr.Zero)
            {
                Log.Warn($"pin: CoCreateInstance(ImmersiveShell) failed hr=0x{hr:X8}");
                return false;
            }

            var queryService = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)
                ReadSlot(serviceProvider, IServiceProvider_QueryService);

            // Try direct QueryService for PinnedApps first - works on Win10
            // and some Win11 builds.
            fixed (Guid* sid = &SID_VirtualDesktopPinnedApps)
            fixed (Guid* iid = &IID_IVirtualDesktopPinnedApps)
            {
                hr = queryService(serviceProvider, sid, iid, &pinnedApps);
            }

            // Fallback A: CoCreateInstance using the IID as the CLSID. Many
            // shell singletons (including IVirtualDesktopPinnedApps on some
            // Win11 builds) can be activated this way.
            if (hr < 0 || pinnedApps == IntPtr.Zero)
            {
                var directHr = CoCreateInstance(IID_IVirtualDesktopPinnedApps, IntPtr.Zero,
                    CLSCTX_LOCAL_SERVER, IID_IVirtualDesktopPinnedApps, out pinnedApps);
                Log.Info($"pin: direct CoCreateInstance(IID as CLSID) hr=0x{directHr:X8}");
                if (directHr < 0 || pinnedApps == IntPtr.Zero)
                {
                    pinnedApps = IntPtr.Zero;
                    hr = directHr;
                }
                else
                {
                    hr = 0;
                }
            }

            // Fallback B: go via IVirtualDesktopManagerInternal then QI.
            // Works on some Win11 builds where the manager interface
            // aggregates PinnedApps.
            if (pinnedApps == IntPtr.Zero)
            {
                Log.Info($"pin: trying via IVirtualDesktopManagerInternal");
                int candidateUsed = -1;
                for (int i = 0; i < IID_IVirtualDesktopManagerInternal_Candidates.Length; i++)
                {
                    fixed (Guid* sid = &SID_VirtualDesktopManagerInternal)
                    fixed (Guid* iid = &IID_IVirtualDesktopManagerInternal_Candidates[i])
                    {
                        hr = queryService(serviceProvider, sid, iid, &managerInternal);
                    }
                    if (hr >= 0 && managerInternal != IntPtr.Zero) { candidateUsed = i; break; }
                }
                if (managerInternal == IntPtr.Zero)
                {
                    Log.Warn($"pin: no IVirtualDesktopManagerInternal IID matched (last hr=0x{hr:X8})");
                    return false;
                }
                Log.Info($"pin: ManagerInternal acquired with IID candidate {candidateUsed}");

                var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
                    ReadSlot(managerInternal, 0 /* QueryInterface */);
                fixed (Guid* iid = &IID_IVirtualDesktopPinnedApps)
                {
                    hr = qi(managerInternal, iid, &pinnedApps);
                }
                if (hr < 0 || pinnedApps == IntPtr.Zero)
                {
                    Log.Warn($"pin: QI(IVirtualDesktopPinnedApps) on ManagerInternal failed hr=0x{hr:X8} - this Windows build doesn't expose pinning via known paths");
                    return false;
                }
            }

            appIdPtr = Marshal.StringToCoTaskMemUni(OverlayAppUserModelId);

            // Skip the call entirely if it's already pinned to avoid
            // duplicate-pin no-op churn on every overlay restart.
            var isPinned = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)
                ReadSlot(pinnedApps, IVirtualDesktopPinnedApps_IsAppIdPinned);
            int pinnedFlag = 0;
            isPinned(pinnedApps, appIdPtr, &pinnedFlag);
            if (pinnedFlag != 0)
            {
                Log.Info($"pin: AppID {OverlayAppUserModelId} already pinned");
                return true;
            }

            var pinAppId = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)
                ReadSlot(pinnedApps, IVirtualDesktopPinnedApps_PinAppID);
            hr = pinAppId(pinnedApps, appIdPtr);
            if (hr < 0)
            {
                Log.Warn($"pin: PinAppID failed hr=0x{hr:X8}");
                return false;
            }

            Log.Info($"pin: PinAppID success app={OverlayAppUserModelId}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"pin: threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (appIdPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(appIdPtr);
            if (pinnedApps != IntPtr.Zero) Release(pinnedApps);
            if (managerInternal != IntPtr.Zero) Release(managerInternal);
            if (serviceProvider != IntPtr.Zero) Release(serviceProvider);
        }
    }

    private static IntPtr ReadSlot(IntPtr comObject, int slot)
    {
        var vt = *(IntPtr**)comObject;
        return vt[slot];
    }

    private static uint Release(IntPtr comObject)
    {
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)ReadSlot(comObject, 2 /* Release */);
        return fn(comObject);
    }
}
