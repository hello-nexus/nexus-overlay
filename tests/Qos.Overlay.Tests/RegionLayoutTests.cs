using System.Text.Json;
using Qos.Overlay;
using Xunit;

namespace Qos.Overlay.Tests;

public class RegionLayoutTests
{
    [Fact]
    public void TryReadRect_ValidObject_ReturnsRect()
    {
        var json = JsonDocument.Parse(@"{ ""x"": 100, ""y"": 200, ""w"": 240, ""h"": 120 }");
        Assert.True(RegionLayout.TryReadRect(json.RootElement, out var r));
        Assert.Equal(new RegionLayout.Rect(100, 200, 240, 120), r);
    }

    [Fact]
    public void TryReadRect_RoundsFractionalCoords()
    {
        var json = JsonDocument.Parse(@"{ ""x"": 100.4, ""y"": 200.6, ""w"": 240.5, ""h"": 119.51 }");
        Assert.True(RegionLayout.TryReadRect(json.RootElement, out var r));
        // Math.Round defaults to banker's rounding: 240.5 -> 240, 100.4 -> 100, 200.6 -> 201, 119.51 -> 120.
        Assert.Equal(new RegionLayout.Rect(100, 201, 240, 120), r);
    }

    [Fact]
    public void TryReadRect_MissingField_ReturnsFalse()
    {
        var json = JsonDocument.Parse(@"{ ""x"": 100, ""y"": 200, ""w"": 240 }");
        Assert.False(RegionLayout.TryReadRect(json.RootElement, out _));
    }

    [Fact]
    public void TryReadRect_ZeroWidth_ReturnsFalse()
    {
        var json = JsonDocument.Parse(@"{ ""x"": 100, ""y"": 200, ""w"": 0, ""h"": 120 }");
        Assert.False(RegionLayout.TryReadRect(json.RootElement, out _));
    }

    [Fact]
    public void TryReadRect_NegativeHeight_ReturnsFalse()
    {
        var json = JsonDocument.Parse(@"{ ""x"": 100, ""y"": 200, ""w"": 240, ""h"": -10 }");
        Assert.False(RegionLayout.TryReadRect(json.RootElement, out _));
    }

    [Fact]
    public void TryReadRect_NonObject_ReturnsFalse()
    {
        var json = JsonDocument.Parse(@"[1, 2, 3, 4]");
        Assert.False(RegionLayout.TryReadRect(json.RootElement, out _));
    }

    [Fact]
    public void TryReadRect_NonNumericField_ReturnsFalse()
    {
        var json = JsonDocument.Parse(@"{ ""x"": ""bad"", ""y"": 200, ""w"": 240, ""h"": 120 }");
        Assert.False(RegionLayout.TryReadRect(json.RootElement, out _));
    }

    [Fact]
    public void CollectRects_WidgetsOnly()
    {
        var json = JsonDocument.Parse(@"{
            ""type"": ""reportLayout"",
            ""widgets"": [
                { ""x"": 10, ""y"": 20, ""w"": 100, ""h"": 50 },
                { ""x"": 200, ""y"": 30, ""w"": 80, ""h"": 60 }
            ]
        }");
        var rects = RegionLayout.CollectRects(json.RootElement);
        Assert.Equal(2, rects.Count);
        Assert.Equal(new RegionLayout.Rect(10, 20, 100, 50), rects[0]);
        Assert.Equal(new RegionLayout.Rect(200, 30, 80, 60), rects[1]);
    }

    [Fact]
    public void CollectRects_WidgetsAndPopover()
    {
        var json = JsonDocument.Parse(@"{
            ""widgets"": [ { ""x"": 10, ""y"": 20, ""w"": 100, ""h"": 50 } ],
            ""popover"": { ""x"": 300, ""y"": 400, ""w"": 200, ""h"": 80 }
        }");
        var rects = RegionLayout.CollectRects(json.RootElement);
        Assert.Equal(2, rects.Count);
        Assert.Equal(new RegionLayout.Rect(10, 20, 100, 50), rects[0]);
        Assert.Equal(new RegionLayout.Rect(300, 400, 200, 80), rects[1]);
    }

    [Fact]
    public void CollectRects_SkipsInvalidEntries()
    {
        // Mixed valid/invalid widgets: the invalid one (missing 'h') is
        // dropped silently, matching the production loop's behavior.
        var json = JsonDocument.Parse(@"{
            ""widgets"": [
                { ""x"": 10, ""y"": 20, ""w"": 100, ""h"": 50 },
                { ""x"": 200, ""y"": 30, ""w"": 80 },
                { ""x"": 400, ""y"": 50, ""w"": 60, ""h"": 60 }
            ]
        }");
        var rects = RegionLayout.CollectRects(json.RootElement);
        Assert.Equal(2, rects.Count);
        Assert.Equal(10, rects[0].X);
        Assert.Equal(400, rects[1].X);
    }

    [Fact]
    public void CollectRects_NoWidgetsArray_ReturnsEmpty()
    {
        var json = JsonDocument.Parse(@"{ ""type"": ""reportLayout"" }");
        var rects = RegionLayout.CollectRects(json.RootElement);
        Assert.Empty(rects);
    }

    [Fact]
    public void CollectRects_PopoverWithoutWidgets()
    {
        // SPA can send a popover-only payload (e.g. context menu open while
        // no widgets pinned). The carve-out should still cover the popover.
        var json = JsonDocument.Parse(@"{
            ""popover"": { ""x"": 0, ""y"": 0, ""w"": 50, ""h"": 50 }
        }");
        var rects = RegionLayout.CollectRects(json.RootElement);
        Assert.Single(rects);
    }

    [Fact]
    public void CollectRects_InvalidPopover_SkippedNotThrown()
    {
        var json = JsonDocument.Parse(@"{
            ""widgets"": [],
            ""popover"": { ""x"": 0, ""y"": 0 }
        }");
        var rects = RegionLayout.CollectRects(json.RootElement);
        Assert.Empty(rects);
    }
}
