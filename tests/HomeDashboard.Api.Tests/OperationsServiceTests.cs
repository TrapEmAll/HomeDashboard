using System.Net;
using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class OperationsServiceTests
{
    [Fact]
    public async Task Snapshot_builds_incidents_and_activity_without_optional_integrations()
    {
        var service = CreateService(out _);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var incident = Assert.Single(snapshot.Incidents);
        Assert.Equal("Plex", incident.ServiceName);
        Assert.Contains(snapshot.Activity, item => item.Title == AuditEventType.RestartCompleted.ToString());
        Assert.Empty(snapshot.Calendar);
        Assert.Empty(snapshot.PlaybackSessions);
    }

    [Fact]
    public void Maintenance_windows_survive_service_recreation()
    {
        var service = CreateService(out var options);
        var starts = DateTimeOffset.UtcNow.AddHours(1);
        service.AddMaintenance(new CreateMaintenanceWindowRequest("Patch window", starts, starts.AddHours(1), null, true), "test");

        var recreated = CreateService(options);

        Assert.Equal("Patch window", Assert.Single(recreated.GetMaintenance()).Title);
    }

    private static OperationsService CreateService(out DashboardOptions options)
    {
        options = new DashboardOptions
        {
            IncludeRecommendedFeeds = false,
            DataPath = Path.Combine(Path.GetTempPath(), "homedashboard-tests", Guid.NewGuid().ToString("n"), "state.json")
        };
        return CreateService(options);
    }

    private static OperationsService CreateService(DashboardOptions options)
        => new(
            Options.Create(options),
            new OfflineServiceProvider(),
            new AuditStore(),
            new ClientFactory(),
            NullLogger<OperationsService>.Instance);

    private sealed class OfflineServiceProvider : IServiceStatusProvider
    {
        public Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ServiceCard>>([
                new ServiceCard("plex", "Plex", ServiceKind.Plex, "Media", null, ServiceStatus.Offline, false, DateTimeOffset.UtcNow, "Connection refused.", [])
            ]);
    }

    private sealed class AuditStore : IAgentCommandStore
    {
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request) => throw new NotSupportedException();
        public AgentCommand? DequeueNext(string agentId) => null;
        public void Complete(string agentId, string commandId, AgentCommandCompletion completion) => throw new NotSupportedException();
        public IReadOnlyList<AgentCommand> GetRecentCommands(int count) => [];
        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count) => [new("audit", AuditEventType.RestartCompleted, "Plex restarted.", "plex", "server-pc", "test", DateTimeOffset.UtcNow)];
        public void AddAuditEvent(AuditEvent auditEvent) { }
    }

    private sealed class ClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler()) { Timeout = TimeSpan.FromSeconds(1) };
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
