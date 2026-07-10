using Nexus.Overlay.Capture;
using Xunit;

namespace Nexus.Overlay.Tests;

public class StreamHostPlanTests
{
    [Fact]
    public void Spawns_desired_sessions_without_hosts()
    {
        var (spawn, close) = StreamHostPlan.Compute(
            desiredSessionIds: new[] { "sess-a", "sess-b" },
            existingSessionIds: new[] { "sess-a" });

        Assert.Equal(new[] { "sess-b" }, spawn);
        Assert.Empty(close);
    }

    [Fact]
    public void Closes_hosts_whose_session_was_removed()
    {
        var (spawn, close) = StreamHostPlan.Compute(
            desiredSessionIds: new[] { "sess-a" },
            existingSessionIds: new[] { "sess-a", "sess-b" });

        Assert.Empty(spawn);
        Assert.Equal(new[] { "sess-b" }, close);
    }

    [Fact]
    public void Spawns_and_closes_in_one_pass()
    {
        var (spawn, close) = StreamHostPlan.Compute(
            desiredSessionIds: new[] { "sess-a", "sess-c" },
            existingSessionIds: new[] { "sess-a", "sess-b" });

        Assert.Equal(new[] { "sess-c" }, spawn);
        Assert.Equal(new[] { "sess-b" }, close);
    }

    [Fact]
    public void Identical_sets_are_a_no_op()
    {
        var (spawn, close) = StreamHostPlan.Compute(
            desiredSessionIds: new[] { "sess-a", "sess-b" },
            existingSessionIds: new[] { "sess-b", "sess-a" });

        Assert.Empty(spawn);
        Assert.Empty(close);
    }

    [Fact]
    public void Empty_desired_closes_everything()
    {
        var (spawn, close) = StreamHostPlan.Compute(
            desiredSessionIds: new string[0],
            existingSessionIds: new[] { "sess-b", "sess-a" });

        Assert.Empty(spawn);
        Assert.Equal(new[] { "sess-a", "sess-b" }, close);
    }

    [Fact]
    public void Output_is_sorted_ordinal()
    {
        var (spawn, close) = StreamHostPlan.Compute(
            desiredSessionIds: new[] { "sess-z", "sess-B", "sess-a" },
            existingSessionIds: new[] { "gone-z", "gone-B", "gone-a" });

        Assert.Equal(new[] { "sess-B", "sess-a", "sess-z" }, spawn);
        Assert.Equal(new[] { "gone-B", "gone-a", "gone-z" }, close);
    }
}
