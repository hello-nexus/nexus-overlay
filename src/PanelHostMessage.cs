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
    public static readonly HitRegionPlan FullLease = new(HitMode.Full, Array.Empty<RegionLayout.Rect>(), lease: true);

    public HitMode Mode { get; }
    /// <summary>A transient full-window report the page must keep renewing.</summary>
    public bool Lease { get; }
    public IReadOnlyList<RegionLayout.Rect> Rects { get; }

    public HitRegionPlan(HitMode mode, IReadOnlyList<RegionLayout.Rect> rects, bool lease = false)
    {
        Mode = mode;
        Lease = lease;
        Rects = rects;
    }

    public bool Equals(HitRegionPlan? other)
    {
        if (other is null || other.Mode != Mode || other.Lease != Lease || other.Rects.Count != Rects.Count) return false;
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
    Frost,
    Activatable,
}

internal readonly record struct PanelHostMessage(PanelMessageKind Kind, HitRegionPlan? Plan, bool RaiseValue, FrostPlan? Frost = null)
{
    private const string Prefix = "nexus.panel.";
    /// <summary>Posted to the page after the host reset its region or raise state under it.</summary>
    internal const string RequestHitTestJson = "{\"type\":\"nexus.panel.requestHitTest\"}";
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

            if (type == "nexus.panel.activatable"
                && root.TryGetProperty("value", out var act)
                && (act.ValueKind == JsonValueKind.True || act.ValueKind == JsonValueKind.False))
                return new PanelHostMessage(PanelMessageKind.Activatable, null, act.GetBoolean());

            if (type == "nexus.panel.frost")
                return new PanelHostMessage(PanelMessageKind.Frost, null, false, ReadFrost(root, windowWidth, windowHeight));

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

    private static FrostPlan ReadFrost(JsonElement root, int windowWidth, int windowHeight)
    {
        var level = root.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        double? rawPercent = root.TryGetProperty("percent", out var pe) && pe.ValueKind == JsonValueKind.Number && pe.TryGetDouble(out var pv) ? pv : null;
        var percent = FrostPolicy.ResolvePercent(level, rawPercent);
        if (percent <= 0 || windowWidth <= 0 || windowHeight <= 0) return FrostPlan.Empty;
        if (!TryReadDpr(root, out var dpr)) return FrostPlan.Empty;
        if (!root.TryGetProperty("rects", out var arr) || arr.ValueKind != JsonValueKind.Array) return FrostPlan.Empty;
        var rects = ScaleRects(arr, dpr, windowWidth, windowHeight, FrostPolicy.MaxPlates);
        if (rects.Count == 0) return FrostPlan.Empty;
        var radius = 0.0;
        if (root.TryGetProperty("radius", out var r) && r.ValueKind == JsonValueKind.Number
            && r.TryGetDouble(out var rv) && double.IsFinite(rv) && rv > 0)
            radius = rv * dpr;
        return new FrostPlan(percent, FrostPolicy.ClampRadius(radius), rects);
    }

    private static bool TryReadDpr(JsonElement root, out double dpr)
    {
        dpr = 0;
        return root.TryGetProperty("dpr", out var d) && d.ValueKind == JsonValueKind.Number
            && d.TryGetDouble(out dpr) && double.IsFinite(dpr) && dpr > 0 && dpr <= MaxDpr;
    }

    // CSS px rects to window device px: rounded outward, clamped to the window, empties dropped.
    private static List<RegionLayout.Rect> ScaleRects(JsonElement arr, double dpr, int windowWidth, int windowHeight, int max)
    {
        var rects = new List<RegionLayout.Rect>();
        foreach (var el in arr.EnumerateArray())
        {
            if (rects.Count >= max) break;
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
        return rects;
    }

    private static HitRegionPlan ReadPlan(JsonElement root, int windowWidth, int windowHeight)
    {
        var mode = root.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (mode == "full")
        {
            var lease = root.TryGetProperty("lease", out var l) && l.ValueKind == JsonValueKind.True;
            return lease ? HitRegionPlan.FullLease : HitRegionPlan.Full;
        }
        if (mode != "region") return HitRegionPlan.Empty;
        if (windowWidth <= 0 || windowHeight <= 0) return HitRegionPlan.Empty;
        if (!TryReadDpr(root, out var dpr)) return HitRegionPlan.Empty;
        if (!root.TryGetProperty("rects", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return HitRegionPlan.Empty;

        var rects = ScaleRects(arr, dpr, windowWidth, windowHeight, MaxRects);
        return rects.Count == 0 ? HitRegionPlan.Empty : new HitRegionPlan(HitMode.Rects, rects);
    }
}
