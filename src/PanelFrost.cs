using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

/// <summary>The frosted plates a see-through panel wants, in window device px.</summary>
internal sealed class FrostPlan
{
    public static readonly FrostPlan Empty = new(0, 0, Array.Empty<RegionLayout.Rect>());

    /// <summary>Frost strength, zero for none up to one hundred.</summary>
    public int Percent { get; }
    public int RadiusPx { get; }
    public IReadOnlyList<RegionLayout.Rect> Rects { get; }

    public FrostPlan(int percent, int radiusPx, IReadOnlyList<RegionLayout.Rect> rects)
    {
        Percent = percent;
        RadiusPx = radiusPx;
        Rects = rects;
    }

    public bool IsEmpty => Percent <= 0 || Rects.Count == 0;
}

/// <summary>What changes between the plates that exist and the plates now wanted.</summary>
internal readonly record struct FrostDiff(IReadOnlyList<int> Place, int Create, int Destroy, bool Retint)
{
    public bool IsNoop => Place.Count == 0 && Create == 0 && Destroy == 0 && !Retint;
}

internal static class FrostPolicy
{
    /// <summary>One top-level window per plate, so the count is capped.</summary>
    internal const int MaxPlates = 64;
    private const int MaxRadiusPx = 256;
    // The acrylic accent and the border and corner attributes need Windows 11.
    private const int Windows11MinBuild = 22000;

    public static bool IsSupportedBuild(int build) => build >= Windows11MinBuild;

    public static int ClampRadius(double radiusPx)
        => double.IsFinite(radiusPx) ? (int)Math.Clamp(Math.Round(radiusPx), 0, MaxRadiusPx) : 0;

    /// <summary>Corner radius for one plate: never more than half its shorter side.</summary>
    public static int RadiusFor(int radiusPx, int width, int height)
        => Math.Clamp(radiusPx, 0, Math.Max(0, Math.Min(width, height) / 2));

    // The slider's strength when only a level is sent.
    private const int LightDefaultPercent = 25;
    private const int HeavyDefaultPercent = 75;
    // Tint alpha at full strength: below opaque so some backdrop always shows through.
    private const int MaxTintAlpha = 0xB3;
    private const uint TintRgb = 0x00181818u;

    /// <summary>
    /// Strength from the message. An explicit percent wins; otherwise the
    /// level picks a default. A level of none, or no usable strength, is off.
    /// </summary>
    public static int ResolvePercent(string? level, double? percent)
    {
        if (level == "none") return 0;
        if (percent is { } p && double.IsFinite(p)) return (int)Math.Clamp(Math.Round(p), 0, 100);
        return level switch { "light" => LightDefaultPercent, "heavy" => HeavyDefaultPercent, _ => 0 };
    }

    /// <summary>Acrylic gradient tint, AABBGGRR: a dark neutral whose alpha scales continuously with strength.</summary>
    public static uint TintFor(int percent)
    {
        var p = Math.Clamp(percent, 0, 100);
        return ((uint)Math.Round(p * MaxTintAlpha / 100.0) << 24) | TintRgb;
    }

    /// <summary>
    /// Diff by plate index so existing windows are reused. Style changes
    /// (strength, radius) re-place every plate, since the radius is baked into
    /// each plate's region.
    /// </summary>
    public static FrostDiff Diff(FrostPlan current, FrostPlan wanted)
    {
        var have = current.IsEmpty ? 0 : current.Rects.Count;
        var want = wanted.IsEmpty ? 0 : wanted.Rects.Count;
        var styleChanged = have > 0 && want > 0
            && (current.Percent != wanted.Percent || current.RadiusPx != wanted.RadiusPx);
        var place = new List<int>();
        for (var i = 0; i < want; i++)
        {
            if (i >= have || styleChanged || current.Rects[i] != wanted.Rects[i]) place.Add(i);
        }
        return new FrostDiff(place, Math.Max(0, want - have), Math.Max(0, have - want),
            Retint: have > 0 && want > 0 && current.Percent != wanted.Percent);
    }
}
