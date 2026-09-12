using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using Nexus.Overlay.WebView2;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Asks the user to install the Edge WebView2 Runtime when the loader finds
/// none; downloads Microsoft's bootstrapper in-process because a box without
/// WebView2 may have no browser to hand a link to.
/// </summary>
internal static unsafe class WebView2RuntimeInstaller
{
    private const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    private const string DownloadPageUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private const string Caption = "Nexus";
    private const uint BoxStyle = Native.MB_ICONWARNING | Native.MB_TOPMOST | Native.MB_SETFOREGROUND;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    private static int _busy;
    private static bool _promptedOnce;
    private static bool _reopenDashboard;

    /// <summary>A prompt or install is in progress; the process must outlive it.</summary>
    public static bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>The loader's answer when it finds no runtime: HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND).</summary>
    public static bool IsRuntimeMissing(int hr) => hr == WebView2Native.E_FILE_NOT_FOUND;

    // The dashboard (userInitiated) asks on every open; the kiosk watchdogs
    // recreate their windows, so those ask once per process.
    public static void OnEnvInitFailed(string surface, int hr, bool userInitiated)
    {
        if (!IsRuntimeMissing(hr)) return;
        if (!userInitiated && _promptedOnce) return;
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _promptedOnce = true;
        _reopenDashboard = userInitiated;
        Log.Warn($"webview2-runtime: not installed (surface={surface}); prompting");
        // Own thread: the message box and the installer wait block, and the
        // caller is mid-window-construction on the UI thread.
        new Thread(PromptAndInstall) { IsBackground = true, Name = "webview2-runtime-install" }.Start();
    }

    private static void PromptAndInstall()
    {
        try
        {
            var answer = Native.MessageBoxW(IntPtr.Zero,
                "Nexus needs the native Microsoft Edge WebView2 Runtime to display its windows, "
                + "and it is not installed on this PC.\n\n"
                + "Download and install it now? (from Microsoft, about 200 MB)",
                Caption, Native.MB_YESNO | BoxStyle);
            if (answer != Native.IDYES)
            {
                Log.Info("webview2-runtime: install declined");
                return;
            }

            var exe = Download();
            if (exe is null)
            {
                Native.MessageBoxW(IntPtr.Zero,
                    "The download failed. Opening Microsoft's WebView2 page instead: "
                    + "install the Evergreen Bootstrapper from there, then open Nexus again.",
                    Caption, Native.MB_OK | BoxStyle);
                var rc = Native.ShellExecuteW(IntPtr.Zero, "open", DownloadPageUrl, null, null, Native.SW_SHOW);
                if (rc.ToInt64() <= 32) Log.Error($"webview2-runtime: ShellExecute of the download page failed rc={rc}");
                return;
            }

            var exit = RunInstaller(exe);
            var version = InstalledVersion();
            Log.Info($"webview2-runtime: bootstrapper exit={exit} runtime={version ?? "(none)"}");
            if (version is not null)
            {
                Program.OnWebView2RuntimeInstalled(_reopenDashboard);
                return;
            }
            Native.MessageBoxW(IntPtr.Zero,
                "The WebView2 Runtime did not install. Run the installer again from Microsoft's "
                + "WebView2 page, then open Nexus again.",
                Caption, Native.MB_OK | BoxStyle);
        }
        catch (Exception ex)
        {
            Log.Error($"webview2-runtime: {ex.Message}");
            Native.MessageBoxW(IntPtr.Zero,
                "The WebView2 Runtime could not be installed: " + ex.Message,
                Caption, Native.MB_OK | BoxStyle);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            Program.OnWebView2RuntimePromptClosed();
        }
    }

    private static string? Download()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Nexus");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "MicrosoftEdgeWebView2Setup.exe");
            using var http = new HttpClient { Timeout = DownloadTimeout };
            using var response = http.GetAsync(BootstrapperUrl).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using (var file = File.Create(path))
            {
                response.Content.CopyToAsync(file).GetAwaiter().GetResult();
            }
            Log.Info($"webview2-runtime: downloaded {new FileInfo(path).Length} bytes to {path}");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error($"webview2-runtime: download failed: {ex.Message}");
            return null;
        }
    }

    // The bootstrapper is asInvoker and elevates its own installer child, so
    // the UAC prompt and the progress window are its; a declined prompt lands
    // here as a nonzero exit with no runtime resolved.
    private static int RunInstaller(string exe)
    {
        if (!AuthenticodeTrust.IsSignedByMicrosoft(exe))
            throw new InvalidOperationException("the downloaded installer is not signed by Microsoft");
        using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        if (p is null) return -1;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Runtime version the loader resolves now, or null while it still finds none.</summary>
    private static string? InstalledVersion()
    {
        var hr = WebView2Native.GetAvailableCoreWebView2BrowserVersionString(null, out var versionInfo);
        if (WebView2Native.Failed(hr) || versionInfo == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(versionInfo); }
        finally { Marshal.FreeCoTaskMem(versionInfo); }
    }
}
