using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Qos.Overlay;

/// <summary>
/// Pure parsing of the SPA's <c>reportLayout</c> webMessage payload.
/// Extracted as a static helper so it's unit-testable on any OS without
/// touching Win32 region APIs.
///
/// Payload shape:
/// <code>
/// {
///   "type": "reportLayout",
///   "widgets": [ { "x": 100, "y": 200, "w": 240, "h": 120 }, ... ],
///   "popover": { "x": 300, "y": 50, "w": 200, "h": 80 }   // optional
/// }
/// </code>
/// </summary>
internal static class RegionLayout
{
    public readonly record struct Rect(int X, int Y, int W, int H);

    /// <summary>
    /// Read a single rect object. Returns false if any field is missing,
    /// non-numeric, or produces a non-positive width/height.
    /// </summary>
    public static bool TryReadRect(JsonElement el, out Rect rect)
    {
        rect = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!el.TryGetProperty("x", out var xEl) || !el.TryGetProperty("y", out var yEl)) return false;
        if (!el.TryGetProperty("w", out var wEl) || !el.TryGetProperty("h", out var hEl)) return false;
        // TryGetDouble throws on non-Number kinds rather than returning false,
        // so kind-check explicitly first - the SPA could send a malformed
        // payload during early init and the host should not crash.
        if (xEl.ValueKind != JsonValueKind.Number || yEl.ValueKind != JsonValueKind.Number
            || wEl.ValueKind != JsonValueKind.Number || hEl.ValueKind != JsonValueKind.Number) return false;
        if (!xEl.TryGetDouble(out var x) || !yEl.TryGetDouble(out var y)
            || !wEl.TryGetDouble(out var w) || !hEl.TryGetDouble(out var h)) return false;
        var rx = (int)Math.Round(x);
        var ry = (int)Math.Round(y);
        var rw = (int)Math.Round(w);
        var rh = (int)Math.Round(h);
        if (rw <= 0 || rh <= 0) return false;
        rect = new Rect(rx, ry, rw, rh);
        return true;
    }

    /// <summary>
    /// Collect every rect referenced by the layout payload. Widgets and
    /// (optional) popover get flattened into a single list in the order
    /// they appeared. Invalid rects are silently skipped (mirrors the
    /// SetWindowRgn loop in OverlayWindow).
    /// </summary>
    public static List<Rect> CollectRects(JsonElement root)
    {
        var rects = new List<Rect>();
        if (root.ValueKind != JsonValueKind.Object) return rects;
        if (root.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array)
        {
            foreach (var widget in widgets.EnumerateArray())
            {
                if (TryReadRect(widget, out var r)) rects.Add(r);
            }
        }
        if (root.TryGetProperty("popover", out var popover) && popover.ValueKind == JsonValueKind.Object)
        {
            if (TryReadRect(popover, out var r)) rects.Add(r);
        }
        return rects;
    }
}
