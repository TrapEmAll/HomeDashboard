using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task GetSnapshotAsync_builds_alerts_from_merged_agent_services()
    {
        var system = new SystemStats("server", 20, 30, [], DateTimeOffset.UtcNow);
        var configured = Card(ServiceStatus.Online, "Configured check passed.");
        var agent = Card(ServiceStatus.Offline, "Windows service stopped.");
        var store = new SnapshotStore(new AgentSnapshot("server-pc", DateTimeOffset.UtcNow, system, [agent]));
        var dashboard = new DashboardService(
            new ServiceProvider(configured),
            new SystemProvider(system),
            new NewsProvider(),
            store,
            store,
            Options.Create(new DashboardOptions { DefaultAgentId = "server-pc" }));

        var snapshot = await dashboard.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(ServiceStatus.Offline, Assert.Single(snapshot.Services).Status);
        var alert = Assert.Single(snapshot.Notifications);
        Assert.Equal("Plex is Offline", alert.Title);
        Assert.Equal("Windows service stopped.", alert.Message);
    }

    private static ServiceCard Card(ServiceStatus status, string message)
        => new("plex", "Plex", ServiceKind.Plex, "Media", null, status, false, DateTimeOffset.UtcNow, message, []);

    private sealed class ServiceProvider(ServiceCard card) : IServiceStatusProvider
    {
        public Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ServiceCard>>([card]);
    }

    private sealed class SystemProvider(SystemStats stats) : ISystemStatsProvider
    {
        public SystemStats GetStats() => stats;
    }

    private sealed class NewsProvider : INewsProvider
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<NewsItem>>([]);
    }

    private sealed class SnapshotStore(AgentSnapshot snapshot) : IAgentSnapshotStore, IAgentCommandStore
    {
        public AgentSnapshot? GetLatest(string agentId) => snapshot;
        public IReadOnlyList<AgentSummary> GetAll() => [];
        public IReadOnlyList<AgentHistoryPoint> GetHistory(string agentId) => [];
        public void Save(AgentSnapshot value) => throw new NotSupportedException();
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request) => throw new NotSupportedException();
        public AgentCommand? DequeueNext(string agentId) => null;
        public void Complete(string agentId, string commandId, AgentCommandCompletion completion) => throw new NotSupportedException();
        public IReadOnlyList<AgentCommand> GetRecentCommands(int count) => [];
        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count) => [];
        public void AddAuditEvent(AuditEvent auditEvent) => throw new NotSupportedException();
    }
}
