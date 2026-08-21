using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class AgentSnapshotStoreTests
{
    [Fact]
    public void Save_replaces_latest_snapshot_for_agent()
    {
        var store = CreateStore();
        var first = Snapshot("server-pc", 5);
        var second = Snapshot("server-pc", 25);

        store.Save(first);
        store.Save(second);

        Assert.Equal(25, store.GetLatest("server-pc")?.System.CpuPercent);
        Assert.Equal(2, store.GetHistory("server-pc").Count);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public void Commands_are_dequeued_and_completed()
    {
        var store = CreateStore();
        var command = store.Enqueue("server-pc", "plex", new RestartRequest("test", "maintenance"));

        var next = store.DequeueNext("server-pc");
        store.Complete("server-pc", command.Id, new AgentCommandCompletion(true, "done"));

        Assert.Equal(command.Id, next?.Id);
        Assert.Equal(AgentCommandState.Running, next?.State);
    }

    private static AgentSnapshot Snapshot(string agentId, double cpuPercent)
        => new(
            agentId,
            DateTimeOffset.UtcNow,
            new SystemStats(agentId, cpuPercent, 10, [], DateTimeOffset.UtcNow),
            []);

    private static FileDashboardStateStore CreateStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "homedashboard-tests", $"{Guid.NewGuid():n}.json");
        return new FileDashboardStateStore(Options.Create(new DashboardOptions { DataPath = path }));
    }
}
