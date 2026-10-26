using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Nexus.Overlay;

/// <summary>What the click-through window region should be.</summary>
internal enum HitMode { Empty, Full, Rects }

internal sealed class HitRegionPlan : IEquatable<HitRegionPlan>
{
    public static readonly HitRegionPlan Empty = new(HitMode.Empty, Array.Empty<RegionLayout.Rect>());
    public static readonly HitRegionPlan Full = new(HitMode.Full, Array.Empty<RegionLayout.Rect>());

    public HitMode Mode { get; }
    public IReadOnlyList<RegionLayout.Rect> Rects { get; }

    public HitRegionPlan(HitMode mode, IReadOnlyList<RegionLayout.Rect> rects)
    {
        Mode = mode;
        Rects = rects;
    }

    public bool Equals(HitRegionPlan? other)
    {
        if (other is null || other.Mode != Mode || other.Rects.Count != Rects.Count) return false;
        for (var i = 0; i < Rects.Count; i++)
        {
            if (Rects[i] != other.Rects[i]) return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as HitRegionPlan);

    public override int GetHashCode() => HashCode.Combine(Mode, Rects.Count);
}

internal enum PanelMessageKind
{
    /// <summary>Not a <c>nexus.panel.*</c> message: route it to the widget host.</summary>
    NotPanel,
    /// <summary>A <c>nexus.panel.*</c> message that is unknown or malformed: drop it.</summary>
    Ignored,
    HitTest,
    Raise,
}

internal readonly record struct PanelHostMessage(PanelMessageKind Kind, HitRegionPlan? Plan, bool RaiseValue)
{
    private const string Prefix = "nexus.panel.";
    // A page reporting more cards than this is broken or hostile; the rest are dropped.
    internal const int MaxRects = 256;
    private const double MaxDpr = 16;
    private const double CoordLimit = 1_000_000;

    /// <summary>
    /// Parse a kiosk web message. Hit-test rects arrive in CSS px relative to
    /// the page; they are scaled by <c>dpr</c> to device px, rounded outward,
    /// and clamped to the window. Anything unusable yields an Empty plan, so a
    /// bad report can only make the window click-through, never capture input.
    /// </summary>
    public static PanelHostMessage Parse(string? json, int windowWidth, int windowHeight)
    {
        var notPanel = new PanelHostMessage(PanelMessageKind.NotPanel, null, false);
        if (string.IsNullOrEmpty(json)) return notPanel;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String) return notPanel;
            var type = typeEl.GetString();
            if (type is null || !type.StartsWith(Prefix, StringComparison.Ordinal)) return notPanel;

            if (type == "nexus.panel.hitTest")
                return new PanelHostMessage(PanelMessageKind.HitTest, ReadPlan(root, windowWidth, windowHeight), false);

            if (type == "nexus.panel.raise"
                && root.TryGetProperty("value", out var v)
                && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
                return new PanelHostMessage(PanelMessageKind.Raise, null, v.GetBoolean());

            return new PanelHostMessage(PanelMessageKind.Ignored, null, false);
        }
        catch (JsonException)
        {
            return notPanel;
        }
    }

    private static HitRegionPlan ReadPlan(JsonElement root, int windowWidth, int windowHeight)
    {
        var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (mode == "full") return HitRegionPlan.Full;
        if (mode != "region") return HitRegionPlan.Empty;
        if (windowWidth <= 0 || windowHeight <= 0) return HitRegionPlan.Empty;
        if (!root.TryGetProperty("dpr", out var d) || d.ValueKind != JsonValueKind.Number
            || !d.TryGetDouble(out var dpr) || !double.IsFinite(dpr) || dpr <= 0 || dpr > MaxDpr)
            return HitRegionPlan.Empty;
        if (!root.TryGetProperty("rects", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return HitRegionPlan.Empty;

        var rects = new List<RegionLayout.Rect>();
        foreach (var el in arr.EnumerateArray())
        {
            if (rects.Count >= MaxRects) break;
            if (!RegionLayout.TryReadRectF(el, out var x, out var y, out var w, out var h)) continue;
            var left = (int)Math.Floor(Math.Clamp(x * dpr, -CoordLimit, CoordLimit));
            var top = (int)Math.Floor(Math.Clamp(y * dpr, -CoordLimit, CoordLimit));
            var right = (int)Math.Ceiling(Math.Clamp((x + w) * dpr, -CoordLimit, CoordLimit));
            var bottom = (int)Math.Ceiling(Math.Clamp((y + h) * dpr, -CoordLimit, CoordLimit));
            left = Math.Clamp(left, 0, windowWidth);
            right = Math.Clamp(right, 0, windowWidth);
            top = Math.Clamp(top, 0, windowHeight);
            bottom = Math.Clamp(bottom, 0, windowHeight);
            if (right <= left || bottom <= top) continue;
            rects.Add(new RegionLayout.Rect(left, top, right - left, bottom - top));
        }
        return rects.Count == 0 ? HitRegionPlan.Empty : new HitRegionPlan(HitMode.Rects, rects);
    }
}
