using Xunit;

namespace Nexus.Overlay.Tests;

public class ProcessTreeTests
{
    [Fact]
    public void Finds_children_and_grandchildren_only()
    {
        // 10 = overlay, 20 = its WebView2 browser, 21/22 = renderer + gpu,
        // 30 = a foreign WebView2 browser with its own renderer 31.
        var procs = new List<(int, int)> { (10, 1), (20, 10), (21, 20), (22, 20), (30, 5), (31, 30) };

        Assert.Equal(new HashSet<int> { 20, 21, 22 }, ProcessTree.Descendants(10, procs));
    }

    [Fact]
    public void Survives_a_parent_id_cycle_and_never_returns_the_root()
    {
        var procs = new List<(int, int)> { (0, 0), (10, 20), (20, 10), (30, 20) };

        Assert.Equal(new HashSet<int> { 20, 30 }, ProcessTree.Descendants(10, procs));
    }

    [Fact]
    public void A_leaf_has_no_descendants()
    {
        Assert.Empty(ProcessTree.Descendants(42, new List<(int, int)> { (42, 1), (43, 1) }));
    }
}
