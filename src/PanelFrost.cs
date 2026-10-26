using System;
using System.Collections.Generic;

namespace Nexus.Overlay;

internal enum FrostLevel { None, Light, Heavy }

/// <summary>The frosted plates a see-through panel wants, in window device px.</summary>
internal sealed class FrostPlan
{
    public static readonly FrostPlan Empty = new(FrostLevel.None, 0, Array.Empty<RegionLayout.Rect>());

    public FrostLevel Level { get; }
    public int RadiusPx { get; }
    public IReadOnlyList<RegionLayout.Rect> Rects { get; }

    public FrostPlan(FrostLevel level, int radiusPx, IReadOnlyList<RegionLayout.Rect> rects)
    {
        Level = level;
        RadiusPx = radiusPx;
        Rects = rects;
    }

    public bool IsEmpty => Level == FrostLevel.None || Rects.Count == 0;
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

    /// <summary>
    /// Acrylic gradient tint, AABBGGRR: a dark neutral, with the heavy level
    /// far more opaque so less of the backdrop shows through.
    /// </summary>
    public static uint TintFor(FrostLevel level) => level switch
    {
        FrostLevel.Heavy => 0x99181818u,
        FrostLevel.Light => 0x30181818u,
        _ => 0u,
    };

    /// <summary>
    /// Diff by plate index so existing windows are reused. Style changes
    /// (level, radius) re-place every plate, since the radius is baked into
    /// each plate's region.
    /// </summary>
    public static FrostDiff Diff(FrostPlan current, FrostPlan wanted)
    {
        var have = current.IsEmpty ? 0 : current.Rects.Count;
        var want = wanted.IsEmpty ? 0 : wanted.Rects.Count;
        var styleChanged = have > 0 && want > 0
            && (current.Level != wanted.Level || current.RadiusPx != wanted.RadiusPx);
        var place = new List<int>();
        for (var i = 0; i < want; i++)
        {
            if (i >= have || styleChanged || current.Rects[i] != wanted.Rects[i]) place.Add(i);
        }
        return new FrostDiff(place, Math.Max(0, want - have), Math.Max(0, have - want),
            Retint: have > 0 && want > 0 && current.Level != wanted.Level);
    }
}
