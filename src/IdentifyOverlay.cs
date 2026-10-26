using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nexus.Overlay.Win32;

namespace Nexus.Overlay;

/// <summary>
/// Display identify: one layered, click-through, non-activating badge per listed monitor, shown from the
/// service's identify state. Monitors match by the same EDID-stable id the kiosks use.
/// </summary>
internal sealed class IdentifyOverlay
{
    private readonly List<BadgeWindow> _windows = new();
    private long _lastToken;

    public bool Active => _windows.Count > 0;

    /// <summary>Apply the state's identify; a new token restarts, a missing one is left to the running timers.</summary>
    public void Apply(IdentifyState? identify)
    {
        if (identify is null || identify.Token == _lastToken || identify.Displays.Count == 0) return;
        _lastToken = identify.Token;
        CloseAll();

        var byId = new Dictionary<string, MonitorInfo>(StringComparer.Ordinal);
        foreach (var m in Monitors.Enumerate())
        {
            var id = DisplayIdentity.ResolveStableId(m.DeviceName);
            if (!string.IsNullOrEmpty(id)) byId.TryAdd(id, m);
        }
        var totalMs = Math.Max(400, identify.RemainingMs);
        foreach (var entry in identify.Displays)
        {
            if (!byId.TryGetValue(entry.DisplayId, out var monitor)) continue;
            try { _windows.Add(new BadgeWindow(this, monitor, entry, totalMs)); }
            catch (Exception ex) { Log.Error($"identify badge: {ex.Message}"); }
        }
        Log.Info($"identify token={identify.Token} badges={_windows.Count}");
    }

    public void CloseAll()
    {
        foreach (var w in _windows.ToArray()) w.Close();
        _windows.Clear();
    }

    private void Forget(BadgeWindow w) => _windows.Remove(w);

    private sealed unsafe class BadgeWindow : IWin32WindowOwner
    {
        private const string ClassName = "Nexus.Overlay.IdentifyBadge";
        private const uint TimerId = 1;
        private const uint TickMs = 16;
        private const long WsExLayered = 0x00080000;
        private const long WsExTransparent = 0x00000020;
        private const uint LwaAlpha = 0x2;
        private const uint WdaExcludeFromCapture = 0x11;
        private const int MaMoNoActivate = 3;
        private const uint WmPaint = 0x000F;
        private const uint WmEraseBkgnd = 0x0014;

        private readonly IdentifyOverlay _owner;
        private readonly Native.RECT _bounds;
        private readonly string _label;
        private readonly string _name;
        private readonly int _side;
        private readonly long _totalMs;
        private readonly long _startTick = Environment.TickCount64;
        private IntPtr _hwnd;
        private int _curSize;

        public BadgeWindow(IdentifyOverlay owner, MonitorInfo monitor, IdentifyEntry entry, long totalMs)
        {
            _owner = owner;
            _bounds = monitor.Bounds;
            _label = entry.Label;
            _name = entry.Name ?? "";
            _totalMs = totalMs;
            _side = IdentifyBadge.Side(_bounds.Width, _bounds.Height);
            _curSize = _side;

            var ex = (uint)(Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | WsExLayered | WsExTransparent);
            _hwnd = Win32Window.Create(ClassName, string.Empty, Native.WS_POPUP, ex,
                _bounds.Left, _bounds.Top, 1, 1, this);
            SetLayeredWindowAttributes(_hwnd, 0, 0, LwaAlpha);
            // Keeps the badge out of screenshots and recordings; older builds simply ignore the failure.
            SetWindowDisplayAffinity(_hwnd, WdaExcludeFromCapture);
            Layout(IdentifyBadge.Frame(0, _totalMs));
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.SetTimer(_hwnd, (UIntPtr)TimerId, TickMs, IntPtr.Zero);
        }

        public void Close()
        {
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero) return;
            _hwnd = IntPtr.Zero;
            Native.KillTimer(hwnd, (UIntPtr)TimerId);
            Win32Window.Unregister(hwnd);
            Native.DestroyWindow(hwnd);
        }

        private void Layout((double Scale, byte Alpha) frame)
        {
            var size = Math.Max(8, (int)Math.Round(_side * frame.Scale));
            var x = _bounds.Left + (_bounds.Width - size) / 2;
            var y = _bounds.Top + (_bounds.Height - size) / 2;
            _curSize = size;
            Native.SetWindowPos(_hwnd, (IntPtr)(-1) /* HWND_TOPMOST */, x, y, size, size, Native.SWP_NOACTIVATE);
            var radius = size / 5;
            Native.SetWindowRgn(_hwnd, Native.CreateRoundRectRgn(0, 0, size + 1, size + 1, radius * 2, radius * 2), true);
            SetLayeredWindowAttributes(_hwnd, 0, frame.Alpha, LwaAlpha);
            InvalidateRect(_hwnd, IntPtr.Zero, false);
        }

        public IntPtr? HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Native.WM_TIMER:
                    {
                        var elapsed = Environment.TickCount64 - _startTick;
                        if (IdentifyBadge.IsDone(elapsed, _totalMs))
                        {
                            _owner.Forget(this);
                            Close();
                            return IntPtr.Zero;
                        }
                        Layout(IdentifyBadge.Frame(elapsed, _totalMs));
                        return IntPtr.Zero;
                    }
                case Native.WM_NCHITTEST:
                    return (IntPtr)Native.HTTRANSPARENT;
                case Native.WM_MOUSEACTIVATE:
                    return (IntPtr)MaMoNoActivate;
                case WmEraseBkgnd:
                    return (IntPtr)1;
                case WmPaint:
                    Paint(hwnd);
                    return IntPtr.Zero;
                default:
                    return null;
            }
        }

        private static uint Rgb(int r, int g, int b) => (uint)(r | (g << 8) | (b << 16));

        private void Paint(IntPtr hwnd)
        {
            var ps = new PAINTSTRUCT();
            var hdc = BeginPaint(hwnd, &ps);
            if (hdc == IntPtr.Zero) return;
            var size = _curSize;
            var mem = CreateCompatibleDC(hdc);
            var bmp = CreateCompatibleBitmap(hdc, size, size);
            var oldBmp = SelectObject(mem, bmp);

            var fill = CreateSolidBrush(Rgb(24, 26, 32));
            var rect = new Native.RECT { Left = 0, Top = 0, Right = size, Bottom = size };
            FillRect(mem, &rect, fill);
            DeleteObject(fill);

            // Thin ring just inside the rounded edge.
            var ringW = Math.Max(2, size / 90);
            var pen = CreatePen(0 /* PS_SOLID */, ringW, Rgb(235, 238, 245));
            var oldPen = SelectObject(mem, pen);
            var oldBrush = SelectObject(mem, Native.GetStockObject(5 /* NULL_BRUSH */));
            var inset = ringW / 2 + 1;
            var corner = (size / 5) * 2;
            RoundRect(mem, inset, inset, size - inset, size - inset, corner, corner);
            SelectObject(mem, oldBrush);
            SelectObject(mem, oldPen);
            DeleteObject(pen);

            SetBkMode(mem, 1 /* TRANSPARENT */);
            var hasName = _name.Length > 0;
            var numberBox = new Native.RECT
            {
                Left = 0,
                Top = 0,
                Right = size,
                Bottom = hasName ? (int)(size * 0.74) : size,
            };
            DrawText(mem, _label, (int)(size * 0.5), 900, numberBox, 0x25 /* center, vcenter, singleline */, Rgb(0, 0, 0), size / 60 + 1);
            DrawText(mem, _label, (int)(size * 0.5), 900, numberBox, 0x25, Rgb(255, 255, 255), 0);
            if (hasName)
            {
                var nameBox = new Native.RECT { Left = size / 12, Top = (int)(size * 0.70), Right = size - size / 12, Bottom = (int)(size * 0.9) };
                DrawText(mem, _name, Math.Max(10, size / 14), 600, nameBox, 0x25 | 0x8000 /* DT_END_ELLIPSIS */, Rgb(200, 205, 215), 0);
            }

            BitBlt(hdc, 0, 0, size, size, mem, 0, 0, 0x00CC0020 /* SRCCOPY */);
            SelectObject(mem, oldBmp);
            DeleteObject(bmp);
            DeleteDC(mem);
            EndPaint(hwnd, &ps);
        }

        private static void DrawText(IntPtr hdc, string text, int heightPx, int weight, Native.RECT box, uint flags, uint color, int shadowOffset)
        {
            var font = CreateFontW(-heightPx, 0, 0, 0, weight, 0, 0, 0, 1 /* DEFAULT_CHARSET */, 0, 0,
                5 /* CLEARTYPE_QUALITY */, 0, "Segoe UI");
            var old = SelectObject(hdc, font);
            SetTextColor(hdc, color);
            if (shadowOffset > 0)
            {
                box.Left += shadowOffset; box.Right += shadowOffset; box.Top += shadowOffset; box.Bottom += shadowOffset;
            }
            DrawTextW(hdc, text, -1, &box, flags);
            SelectObject(hdc, old);
            DeleteObject(font);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public Native.RECT rcPaint;
            public int fRestore;
            public int fIncUpdate;
            public fixed byte rgbReserved[32];
        }

        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, PAINTSTRUCT* ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, PAINTSTRUCT* ps);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr hdc, Native.RECT* rect, IntPtr brush);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr hdc, string text, int count, Native.RECT* rect, uint format);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
        [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
        [DllImport("gdi32.dll")] private static extern bool RoundRect(IntPtr hdc, int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr hdc, uint color);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
            uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitchFamily, string face);
    }
}
