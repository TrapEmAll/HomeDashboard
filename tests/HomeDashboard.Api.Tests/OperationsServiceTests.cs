using System.Net;
using System.Text;
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

    [Fact]
    public async Task Snapshot_reuses_recent_integration_results()
    {
        var options = new DashboardOptions
        {
            IncludeRecommendedFeeds = false,
            DataPath = Path.Combine(Path.GetTempPath(), "homedashboard-tests", Guid.NewGuid().ToString("n"), "state.json")
        };
        var services = new CountingServiceProvider();
        var service = new OperationsService(Options.Create(options), services, new AuditStore(), new ClientFactory(), NullLogger<OperationsService>.Instance);

        var first = await service.GetSnapshotAsync(CancellationToken.None);
        var second = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, services.RequestCount);
    }

    [Fact]
    public async Task Snapshot_normalizes_arr_health_queue_history_and_missing_counts()
    {
        var options = ArrOptions();
        var handler = new ArrHandler();
        var service = new OperationsService(Options.Create(options), new CountingServiceProvider(), new RecordingAuditStore(),
            new StubClientFactory(handler), NullLogger<OperationsService>.Instance);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        var instance = Assert.Single(snapshot.Arr.Instances);
        Assert.True(instance.Connected);
        Assert.Equal("4.0.1", instance.Version);
        Assert.Equal(1, instance.QueueCount);
        Assert.Equal(1, instance.HealthIssueCount);
        Assert.Equal(7, instance.MissingCount);
        Assert.Equal(50, Assert.Single(snapshot.Arr.Queue).ProgressPercent);
        Assert.Equal("Example Series", Assert.Single(snapshot.Arr.History).Title);
        Assert.All(handler.ApiKeys, key => Assert.Equal("sonarr-key", key));
    }

    [Fact]
    public async Task Missing_search_requires_confirmation_and_sends_audited_arr_command()
    {
        var handler = new ArrHandler();
        var audit = new RecordingAuditStore();
        var service = new OperationsService(Options.Create(ArrOptions()), new CountingServiceProvider(), audit,
            new StubClientFactory(handler), NullLogger<OperationsService>.Instance);

        var proposed = await service.RunArrCommandAsync(new ArrCommandRequest("sonarr", ArrCommandAction.SearchMissing), "owner", CancellationToken.None);
        var accepted = await service.RunArrCommandAsync(new ArrCommandRequest("sonarr", ArrCommandAction.SearchMissing, true), "owner", CancellationToken.None);

        Assert.True(proposed.RequiresConfirmation);
        Assert.True(accepted.Succeeded);
        Assert.Contains(handler.CommandBodies, body => body.Contains("MissingEpisodeSearch", StringComparison.Ordinal));
        Assert.Contains(audit.Events, item => item.Type == AuditEventType.MediaCommand && item.Succeeded && item.Actor == "owner");
    }

    private static DashboardOptions ArrOptions() => new()
    {
        IncludeRecommendedFeeds = false,
        DataPath = Path.Combine(Path.GetTempPath(), "homedashboard-tests", Guid.NewGuid().ToString("n"), "state.json"),
        Services = [new ServiceDefinition
        {
            Id = "sonarr", Name = "Sonarr", Kind = ServiceKind.Sonarr, Url = new Uri("http://sonarr.local:8989"), ApiKey = "sonarr-key"
        }]
    };

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

    private sealed class CountingServiceProvider : IServiceStatusProvider
    {
        public int RequestCount { get; private set; }
        public Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult<IReadOnlyList<ServiceCard>>([]);
        }
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

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(1) };
    }

    private sealed class ArrHandler : HttpMessageHandler
    {
        public List<string?> ApiKeys { get; } = [];
        public List<string> CommandBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKeys.Add(request.Headers.TryGetValues("X-Api-Key", out var values) ? values.Single() : null);
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (request.Method == HttpMethod.Post && path.EndsWith("/command", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                CommandBodies.Add(body);
                json = "{\"id\":1,\"status\":\"queued\"}";
            }
            else if (path.EndsWith("/system/status", StringComparison.Ordinal)) json = "{\"version\":\"4.0.1\"}";
            else if (path.EndsWith("/health", StringComparison.Ordinal)) json = "[{\"source\":\"DownloadClientCheck\",\"type\":\"warning\",\"message\":\"Download client unavailable\"}]";
            else if (path.EndsWith("/queue", StringComparison.Ordinal)) json = "{\"records\":[{\"id\":12,\"series\":{\"title\":\"Example Series\"},\"title\":\"Episode 1\",\"status\":\"downloading\",\"trackedDownloadStatus\":\"ok\",\"size\":1000,\"sizeleft\":500}]}";
            else if (path.EndsWith("/wanted/missing", StringComparison.Ordinal)) json = "{\"totalRecords\":7,\"records\":[]}";
            else if (path.EndsWith("/history", StringComparison.Ordinal)) json = "{\"records\":[{\"id\":13,\"series\":{\"title\":\"Example Series\"},\"eventType\":\"downloadFolderImported\",\"date\":\"2030-01-02T03:04:05Z\",\"quality\":{\"quality\":{\"name\":\"WEB-1080p\"}}}]}";
            else json = "[]";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class RecordingAuditStore : IAgentCommandStore
    {
        public List<AuditEvent> Events { get; } = [];
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request) => throw new NotSupportedException();
        public AgentCommand? DequeueNext(string agentId) => null;
        public void Complete(string agentId, string commandId, AgentCommandCompletion completion) => throw new NotSupportedException();
        public IReadOnlyList<AgentCommand> GetRecentCommands(int count) => [];
        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count) => Events;
        public void AddAuditEvent(AuditEvent auditEvent) => Events.Add(auditEvent);
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
