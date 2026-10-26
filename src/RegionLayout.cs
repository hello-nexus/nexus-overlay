using System.Text.Json;

namespace Nexus.Overlay;

/// <summary>
/// Reads the rect objects of the panel's <c>nexus.panel.hitTest</c> report.
/// No Win32 region APIs, so unit-testable on any OS.
/// </summary>
internal static class RegionLayout
{
    public readonly record struct Rect(int X, int Y, int W, int H);

    /// <summary>
    /// Read a rect object, keeping fractional values for callers that scale
    /// before rounding. Rejects missing or non-numeric fields, non-finite
    /// numbers and a non-positive width or height.
    /// </summary>
    public static bool TryReadRectF(JsonElement el, out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        if (!TryNumber(el, "x", out x) || !TryNumber(el, "y", out y)
            || !TryNumber(el, "w", out w) || !TryNumber(el, "h", out h)) return false;
        return w > 0 && h > 0;
    }

    private static bool TryNumber(JsonElement el, string name, out double value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return false;
        return p.TryGetDouble(out value) && double.IsFinite(value);
    }
}
