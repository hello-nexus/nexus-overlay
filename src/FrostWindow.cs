using System;
using System.Runtime.InteropServices;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// One acrylic blur plate sitting directly under an overlay widget card.
/// The plate is its own top-level window because the acrylic accent fills
/// the whole window rect and ignores SetWindowRgn: a single blurred overlay
/// window would frost the entire monitor. Click-through and never activated.
/// </summary>
internal sealed unsafe class FrostWindow : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.FrostWindow";
    private const uint MA_NOACTIVATE = 3;
    // AABBGGRR. Light neutral tint so the blur stays the main effect; the
    // card fill (opacity slider) sits on top of it.
    private const uint TintColor = 0x30181818;

    public IntPtr Hwnd { get; private set; }

    public static bool IsSupported => Environment.OSVersion.Version.Build >= Native.AcrylicMinBuild;

    /// <summary>Null when the window or the accent could not be created.</summary>
    public static FrostWindow? TryCreate()
    {
        if (!IsSupported) return null;
        var frost = new FrostWindow();
        try
        {
            frost.Hwnd = Win32Window.Create(
                WindowClassName,
                "Nexus Overlay Frost",
                Native.WS_POPUP,
                (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
                0, 0, 1, 1,
                frost);
            frost.ApplyAcrylic();
            return frost;
        }
        catch (Exception ex)
        {
            Log.Warn($"frost window unavailable: {ex.Message}");
            frost.Dispose();
            return null;
        }
    }

    private void ApplyAcrylic()
    {
        var accent = new Native.AccentPolicy
        {
            AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = Native.ACCENT_FLAG_DRAW_ALL_BORDERS,
            GradientColor = TintColor,
            AnimationId = 0,
        };
        var data = new Native.WindowCompositionAttributeData
        {
            Attribute = Native.WCA_ACCENT_POLICY,
            Data = (IntPtr)(&accent),
            SizeOfData = sizeof(Native.AccentPolicy),
        };
        if (Native.SetWindowCompositionAttribute(Hwnd, ref data) == 0)
        {
            throw new InvalidOperationException("SetWindowCompositionAttribute rejected the acrylic accent");
        }
        // Match the card corners (the host clips cards to a rounded region);
        // a no-op on Windows 10.
        int round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, round, sizeof(int));
    }

    /// <summary>Moves and sizes the plate to a screen rect without touching z-order.</summary>
    public void Place(int x, int y, int w, int h)
    {
        if (Hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
    }

    /// <summary>Stacks the plate directly below <paramref name="above"/>.</summary>
    public void Restack(IntPtr above)
    {
        if (Hwnd == IntPtr.Zero) return;
        // Leave the topmost band first so a plate that was topmost does not
        // stay there when the overlay itself sank to the bottom. Inserting
        // after a topmost window re-enters the band.
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(Hwnd, above, 0, 0, 0, 0, flags);
    }

    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST:
                return (IntPtr)Native.HTTRANSPARENT;
            case Native.WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;
            case Native.WM_ERASEBKGND:
                return (IntPtr)1;
        }
        return null;
    }

    public void Dispose()
    {
        var hwnd = Hwnd;
        if (hwnd == IntPtr.Zero) return;
        Hwnd = IntPtr.Zero;
        Native.DestroyWindow(hwnd);
        Win32Window.Unregister(hwnd);
    }
}
