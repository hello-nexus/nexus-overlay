using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Nexus.Overlay.Tests;

public class MonitorMapPublisherTests
{
    private static List<OverlayMonitorEntry> Map(string id) => new() { new OverlayMonitorEntry { Index = 0, DisplayId = id, Primary = true } };

    [Fact]
    public async Task UnchangedMapIsPostedOnce()
    {
        var posts = new List<string>();
        var p = new MonitorMapPublisher(() => Map("a"), e => { posts.Add(e[0].DisplayId); return Task.FromResult(true); });
        await p.RequestAsync();
        await p.RequestAsync();
        Assert.Equal(new[] { "a" }, posts);
    }

    [Fact]
    public async Task BurstDuringFlightCoalescesAndEndsOnLatestMap()
    {
        var posts = new List<string>();
        var current = "a";
        var gate = new TaskCompletionSource<bool>();
        var first = true;
        var p = new MonitorMapPublisher(() => Map(current), async e =>
        {
            posts.Add(e[0].DisplayId);
            if (first) { first = false; await gate.Task; }
            return true;
        });
        var run = p.RequestAsync();
        current = "b";
        await p.RequestAsync();
        current = "c";
        await p.RequestAsync();
        gate.SetResult(true);
        await run;
        Assert.Equal(new[] { "a", "c" }, posts);
    }

    [Fact]
    public async Task FailedPostIsRetriedOnNextRequest()
    {
        var ok = false;
        var posts = 0;
        var p = new MonitorMapPublisher(() => Map("a"), _ => { posts++; return Task.FromResult(ok); });
        await p.RequestAsync();
        ok = true;
        await p.RequestAsync();
        await p.RequestAsync();
        Assert.Equal(2, posts);
    }

    [Fact]
    public async Task InvalidateReposts()
    {
        var posts = 0;
        var p = new MonitorMapPublisher(() => Map("a"), _ => { posts++; return Task.FromResult(true); });
        await p.RequestAsync();
        p.Invalidate();
        await p.RequestAsync();
        Assert.Equal(2, posts);
    }
}
