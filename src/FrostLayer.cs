using System;
using System.Collections.Generic;
using System.Text;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// One acrylic blur plate under a widget card. Its own top-level window,
/// because the acrylic accent fills the whole window rect and on some builds
/// ignores a region set on a shared window. Never takes input or focus and
/// stays out of Alt+Tab and the taskbar.
/// </summary>
internal sealed unsafe class FrostPlate : IWin32WindowOwner, IDisposable
{
    private const string WindowClassName = "Nexus.Overlay.PanelFrost";
    private const uint MA_NOACTIVATE = 3;
    private bool _shown;
    private int _w;
    private int _h;

    public IntPtr Hwnd { get; private set; }

    public static FrostPlate? TryCreate()
    {
        var plate = new FrostPlate();
        try
        {
            plate.Hwnd = Win32Window.Create(
                WindowClassName, "Nexus Panel Frost", Native.WS_POPUP,
                (uint)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
                0, 0, 1, 1, plate);
            // No border and no DWM corner rounding: the rounded region is the only edge.
            Native.DwmSetWindowAttribute(plate.Hwnd, Native.DWMWA_BORDER_COLOR, Native.DWMWA_COLOR_NONE, sizeof(int));
            Native.DwmSetWindowAttribute(plate.Hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, Native.DWMWCP_DONOTROUND, sizeof(int));
            return plate;
        }
        catch (Exception ex)
        {
            Log.Warn($"panel-frost plate unavailable: {ex.Message}");
            plate.Dispose();
            return null;
        }
    }

    public bool SetTint(FrostLevel level)
    {
        var accent = new Native.AccentPolicy
        {
            AccentState = Native.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 0,
            GradientColor = FrostPolicy.TintFor(level),
            AnimationId = 0,
        };
        var data = new Native.WindowCompositionAttributeData
        {
            Attribute = Native.WCA_ACCENT_POLICY,
            Data = (IntPtr)(&accent),
            SizeOfData = sizeof(Native.AccentPolicy),
        };
        return Native.SetWindowCompositionAttribute(Hwnd, ref data) != 0;
    }

    /// <summary>Place at a screen rect and clip to a rounded shape so no blur reaches past the card corners.</summary>
    public void Place(int x, int y, int w, int h, int radiusPx)
    {
        if (Hwnd == IntPtr.Zero) return;
        _w = w;
        _h = h;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOZORDER);
        var r = FrostPolicy.RadiusFor(radiusPx, w, h);
        var region = r > 0 ? Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2) : IntPtr.Zero;
        if (Native.SetWindowRgn(Hwnd, region, false) == 0 && region != IntPtr.Zero) Native.DeleteObject(region);
    }

    /// <summary>Insert directly below <paramref name="kiosk"/>, showing the plate on first use.</summary>
    public void StackBelow(IntPtr kiosk)
    {
        if (Hwnd == IntPtr.Zero) return;
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        // Leave the topmost band first so a plate that was topmost does not stay there
        // when the kiosk sank; inserting after a topmost kiosk re-enters the band.
        Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        Native.SetWindowPos(Hwnd, kiosk, 0, 0, 0, 0, flags);
        if (!_shown)
        {
            _shown = true;
            Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, flags | Native.SWP_NOZORDER | Native.SWP_SHOWWINDOW);
        }
    }

    public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Native.WM_NCHITTEST: return (IntPtr)Native.HTTRANSPARENT;
            case Native.WM_MOUSEACTIVATE: return (IntPtr)MA_NOACTIVATE;
            case Native.WM_ERASEBKGND: return (IntPtr)1;
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

/// <summary>The set of plates behind one kiosk's cards. Message-loop thread only.</summary>
internal sealed class FrostLayer : IDisposable
{
    private readonly List<FrostPlate> _plates = new();
    private FrostPlan _current = FrostPlan.Empty;

    public static bool IsSupported => FrostPolicy.IsSupportedBuild(Environment.OSVersion.Version.Build);

    public int Count => _plates.Count;

    /// <param name="originX">Screen x of the kiosk window's client origin.</param>
    public void Update(FrostPlan wanted, IntPtr kiosk, int originX, int originY)
    {
        var diff = FrostPolicy.Diff(_current, wanted);
        if (diff.IsNoop) return;

        for (var i = 0; i < diff.Destroy; i++) RemoveLast();
        for (var i = 0; i < diff.Create; i++)
        {
            var plate = FrostPlate.TryCreate();
            if (plate is null) break;
            _plates.Add(plate);
        }
        // A plate that could not be created leaves fewer than wanted; the plan
        // recorded is what is actually present so the next update retries.
        var live = Math.Min(_plates.Count, wanted.IsEmpty ? 0 : wanted.Rects.Count);
        var applied = new List<RegionLayout.Rect>();
        for (var i = 0; i < live; i++) applied.Add(wanted.Rects[i]);

        foreach (var i in diff.Place)
        {
            if (i >= live) continue;
            var r = wanted.Rects[i];
            _plates[i].Place(originX + r.X, originY + r.Y, r.W, r.H, wanted.RadiusPx);
        }
        if (!wanted.IsEmpty)
        {
            for (var i = 0; i < live; i++)
            {
                var isNew = i >= (_current.IsEmpty ? 0 : _current.Rects.Count);
                if (isNew || diff.Retint)
                {
                    if (!_plates[i].SetTint(wanted.Level)) Log.Warn("panel-frost acrylic accent rejected");
                }
                _plates[i].StackBelow(kiosk);
            }
        }
        _current = live == 0 ? FrostPlan.Empty : new FrostPlan(wanted.Level, wanted.RadiusPx, applied);
        LogState(kiosk, wanted.Level);
    }

    /// <summary>Re-insert every plate under the kiosk after it was raised, lowered or its topmost state changed.</summary>
    public void Restack(IntPtr kiosk)
    {
        foreach (var plate in _plates) plate.StackBelow(kiosk);
        if (_plates.Count > 0) LogState(kiosk, _current.Level);
    }

    /// <summary>Move every plate after the kiosk window moved or resized.</summary>
    public void Reposition(int originX, int originY)
    {
        for (var i = 0; i < _plates.Count && i < _current.Rects.Count; i++)
        {
            var r = _current.Rects[i];
            _plates[i].Place(originX + r.X, originY + r.Y, r.W, r.H, _current.RadiusPx);
        }
    }

    public void Clear()
    {
        if (_plates.Count == 0 && _current.IsEmpty) return;
        while (_plates.Count > 0) RemoveLast();
        _current = FrostPlan.Empty;
        Log.Info("panel-frost cleared");
    }

    private void RemoveLast()
    {
        var last = _plates[^1];
        _plates.RemoveAt(_plates.Count - 1);
        last.Dispose();
    }

    // Plates were once missing from the window list with always-on-top off and the
    // cause is unknown, so every change logs how many exist and where they sit.
    private void LogState(IntPtr kiosk, FrostLevel level)
    {
        var sb = new StringBuilder();
        sb.Append($"panel-frost level={level} plates={_plates.Count} kioskZ={ZIndex(kiosk)} kioskTopmost={IsTopmost(kiosk)}");
        for (var i = 0; i < _plates.Count && i < 3; i++)
        {
            var h = _plates[i].Hwnd;
            sb.Append($" plate{i}[z={ZIndex(h)} visible={Native.IsWindowVisible(h)} topmost={IsTopmost(h)}]");
        }
        Log.Info(sb.ToString());
    }

    private static bool IsTopmost(IntPtr hwnd)
        => (Native.GetWindowLongW(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOPMOST) != 0;

    private static int ZIndex(IntPtr target)
    {
        var index = 0;
        for (var h = Native.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero && index < 4096; h = Native.GetWindow(h, Native.GW_HWNDNEXT), index++)
        {
            if (h == target) return index;
        }
        return -1;
    }

    public void Dispose() => Clear();
}
