using HomeDashboard.Agent;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Agent.Tests;

public sealed class AgentCollectorTests
{
    [Fact]
    public void Collect_combines_system_and_service_snapshots()
    {
        var collector = new AgentCollector(
            new StaticOptionsMonitor<AgentOptions>(new AgentOptions { AgentId = "server-pc" }),
            new StubSystemCollector(),
            new StubServiceCollector());

        var snapshot = collector.Collect();

        Assert.Equal("server-pc", snapshot.AgentId);
        Assert.Equal("server-pc", snapshot.System.Hostname);
        Assert.Single(snapshot.Services);
        Assert.Equal("plex", snapshot.Services[0].Id);
    }

    [Fact]
    public void System_collector_reuses_expensive_host_details_between_poll_cycles()
    {
        var collector = new SystemSnapshotCollector(new StaticOptionsMonitor<AgentOptions>(new AgentOptions()));

        var first = collector.Collect();
        var second = collector.Collect();

        Assert.Same(first.Disks, second.Disks);
        Assert.Same(first.TopProcesses, second.TopProcesses);
    }

    private sealed class StubSystemCollector : ISystemSnapshotCollector
    {
        public SystemStats Collect()
            => new("server-pc", 5, 20, [], DateTimeOffset.UtcNow);
    }

    private sealed class StubServiceCollector : IWindowsServiceSnapshotCollector
    {
        public IReadOnlyList<ServiceCard> Collect()
            => [new("plex", "Plex", ServiceKind.Plex, "Media server", null, ServiceStatus.Online, false, DateTimeOffset.UtcNow, null, [])];
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
