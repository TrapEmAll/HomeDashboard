using HomeDashboard.Agent;
using HomeDashboard.Contracts;
using Xunit;

namespace HomeDashboard.Agent.Tests;

public sealed class AgentCollectorTests
{
    [Fact]
    public void Collect_combines_system_and_service_snapshots()
    {
        var collector = new AgentCollector(new StubSystemCollector(), new StubServiceCollector());

        var snapshot = collector.Collect();

        Assert.Equal("server-pc", snapshot.System.Hostname);
        Assert.Single(snapshot.Services);
        Assert.Equal("plex", snapshot.Services[0].Id);
    }

    private sealed class StubSystemCollector : ISystemSnapshotCollector
    {
        public SystemStats Collect()
            => new("server-pc", 5, 20, [], DateTimeOffset.UtcNow);
    }

    private sealed class StubServiceCollector : IWindowsServiceSnapshotCollector
    {
        public IReadOnlyList<ServiceCard> Collect()
            => [new("plex", "Plex", "Media server", null, ServiceStatus.Online, false, DateTimeOffset.UtcNow, null)];
    }
}
