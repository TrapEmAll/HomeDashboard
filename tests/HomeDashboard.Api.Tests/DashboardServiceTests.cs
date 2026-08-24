using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public void Local_system_provider_includes_extended_host_telemetry()
    {
        var stats = new LocalSystemStatsProvider().GetStats();

        Assert.True(stats.UptimeSeconds > 0);
        Assert.False(string.IsNullOrWhiteSpace(stats.OsVersion));
        Assert.NotNull(stats.TopProcesses);
        Assert.NotNull(stats.NetworkInterfaces);
    }

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
            Options.Create(new DashboardOptions { DefaultAgentId = "server-pc" }),
            NullLogger<DashboardService>.Instance);

        var snapshot = await dashboard.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal(ServiceStatus.Offline, Assert.Single(snapshot.Services).Status);
        var alert = Assert.Single(snapshot.Notifications);
        Assert.Equal("Plex is Offline", alert.Title);
        Assert.Equal("Windows service stopped.", alert.Message);
    }

    [Fact]
    public async Task GetSnapshotAsync_returns_partial_dashboard_when_optional_providers_fail()
    {
        var store = new EmptyStore();
        var dashboard = new DashboardService(
            new ThrowingServiceProvider(),
            new ThrowingSystemProvider(),
            new ThrowingNewsProvider(),
            store,
            store,
            Options.Create(new DashboardOptions()),
            NullLogger<DashboardService>.Instance);

        var snapshot = await dashboard.GetSnapshotAsync(CancellationToken.None);

        Assert.Empty(snapshot.Services);
        Assert.Empty(snapshot.News);
        Assert.Empty(snapshot.System.Disks);
        Assert.Equal(Environment.MachineName, snapshot.System.Hostname);
    }

    [Fact]
    public async Task GetSnapshotAsync_uses_local_telemetry_when_agent_snapshot_is_stale()
    {
        var staleSystem = new SystemStats("stale-agent", 1, 2, [], DateTimeOffset.UtcNow.AddMinutes(-5));
        var localSystem = new SystemStats("api-host", 42, 55, [], DateTimeOffset.UtcNow, 3600);
        var store = new SnapshotStore(new AgentSnapshot("server-pc", DateTimeOffset.UtcNow.AddMinutes(-5), staleSystem, []));
        var dashboard = new DashboardService(
            new ServiceProvider(Card(ServiceStatus.Online, "Online")),
            new SystemProvider(localSystem),
            new NewsProvider(),
            store,
            store,
            Options.Create(new DashboardOptions { DefaultAgentId = "server-pc" }),
            NullLogger<DashboardService>.Instance);

        var snapshot = await dashboard.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("api-host", snapshot.System.Hostname);
        Assert.Equal(3600, snapshot.System.UptimeSeconds);
        Assert.Same(localSystem, snapshot.ApiSystem);
    }

    [Fact]
    public async Task GetSnapshotAsync_keeps_api_host_telemetry_when_remote_agent_is_active()
    {
        var agentSystem = new SystemStats("remote-agent", 12, 34, [], DateTimeOffset.UtcNow);
        var apiSystem = new SystemStats(
            "api-host", 21, 43, [], DateTimeOffset.UtcNow, NetworkInterfaces:
            [new NetworkInterfaceStats("ethernet", "Ethernet", "Adapter", "Ethernet", "192.168.0.18", 1_000_000_000, 1024, 512, 4, 2, 0, 0, 0, 0)]);
        var store = new SnapshotStore(new AgentSnapshot("server-pc", DateTimeOffset.UtcNow, agentSystem, []));
        var dashboard = new DashboardService(
            new ServiceProvider(Card(ServiceStatus.Online, "Online")),
            new SystemProvider(apiSystem),
            new NewsProvider(),
            store,
            store,
            Options.Create(new DashboardOptions { DefaultAgentId = "server-pc" }),
            NullLogger<DashboardService>.Instance);

        var snapshot = await dashboard.GetSnapshotAsync(CancellationToken.None);

        Assert.Equal("remote-agent", snapshot.System.Hostname);
        Assert.Equal("api-host", snapshot.ApiSystem!.Hostname);
        Assert.Equal("192.168.0.18", Assert.Single(snapshot.ApiSystem.NetworkInterfaces!).Address);
    }

    [Fact]
    public async Task GetSnapshotAsync_reuses_recent_snapshot_work()
    {
        var system = new SystemStats("api-host", 10, 20, [], DateTimeOffset.UtcNow);
        var services = new CountingServiceProvider(Card(ServiceStatus.Online, "Online"));
        var store = new EmptyStore();
        var dashboard = new DashboardService(
            services, new SystemProvider(system), new NewsProvider(), store, store,
            Options.Create(new DashboardOptions()), NullLogger<DashboardService>.Instance);

        var first = await dashboard.GetSnapshotAsync(CancellationToken.None);
        var second = await dashboard.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, services.RequestCount);
    }

    private static ServiceCard Card(ServiceStatus status, string message)
        => new("plex", "Plex", ServiceKind.Plex, "Media", null, status, false, DateTimeOffset.UtcNow, message, []);

    private sealed class ServiceProvider(ServiceCard card) : IServiceStatusProvider
    {
        public Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ServiceCard>>([card]);
    }

    private sealed class CountingServiceProvider(ServiceCard card) : IServiceStatusProvider
    {
        public int RequestCount { get; private set; }
        public Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult<IReadOnlyList<ServiceCard>>([card]);
        }
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

    private sealed class ThrowingServiceProvider : IServiceStatusProvider
    {
        public Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<ServiceCard>>(new InvalidOperationException("service failure"));
    }

    private sealed class ThrowingSystemProvider : ISystemStatsProvider
    {
        public SystemStats GetStats() => throw new InvalidOperationException("system failure");
    }

    private sealed class ThrowingNewsProvider : INewsProvider
    {
        public Task<IReadOnlyList<NewsItem>> GetNewsAsync(CancellationToken cancellationToken)
            => Task.FromException<IReadOnlyList<NewsItem>>(new InvalidOperationException("news failure"));
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

    private sealed class EmptyStore : IAgentSnapshotStore, IAgentCommandStore
    {
        public AgentSnapshot? GetLatest(string agentId) => null;
        public IReadOnlyList<AgentSummary> GetAll() => [];
        public IReadOnlyList<AgentHistoryPoint> GetHistory(string agentId) => [];
        public void Save(AgentSnapshot snapshot) => throw new NotSupportedException();
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request) => throw new NotSupportedException();
        public AgentCommand? DequeueNext(string agentId) => null;
        public void Complete(string agentId, string commandId, AgentCommandCompletion completion) => throw new NotSupportedException();
        public IReadOnlyList<AgentCommand> GetRecentCommands(int count) => [];
        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count) => [];
        public void AddAuditEvent(AuditEvent auditEvent) => throw new NotSupportedException();
    }
}
