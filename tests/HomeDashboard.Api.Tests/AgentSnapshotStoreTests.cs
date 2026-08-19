using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class AgentSnapshotStoreTests
{
    [Fact]
    public void Save_replaces_latest_snapshot_for_agent()
    {
        var store = new InMemoryAgentSnapshotStore();
        var first = Snapshot("server-pc", 5);
        var second = Snapshot("server-pc", 25);

        store.Save(first);
        store.Save(second);

        Assert.Equal(25, store.GetLatest("server-pc")?.System.CpuPercent);
    }

    private static AgentSnapshot Snapshot(string agentId, double cpuPercent)
        => new(
            agentId,
            DateTimeOffset.UtcNow,
            new SystemStats(agentId, cpuPercent, 10, [], DateTimeOffset.UtcNow),
            []);
}
