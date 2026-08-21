using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public interface IDashboardService
{
    Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

public interface IServiceStatusProvider
{
    Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken);
}

public interface ISystemStatsProvider
{
    SystemStats GetStats();
}

public interface INewsProvider
{
    Task<IReadOnlyList<NewsItem>> GetNewsAsync(CancellationToken cancellationToken);
}

public interface IRestartCoordinator
{
    RestartResult QueueRestart(string serviceId, RestartRequest request);
}

public interface IAgentSnapshotStore
{
    AgentSnapshot? GetLatest(string agentId);
    IReadOnlyList<AgentSummary> GetAll();
    IReadOnlyList<AgentHistoryPoint> GetHistory(string agentId);
    void Save(AgentSnapshot snapshot);
}

public interface IAgentCommandStore
{
    AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request);
    AgentCommand? DequeueNext(string agentId);
    void Complete(string agentId, string commandId, AgentCommandCompletion completion);
    IReadOnlyList<AgentCommand> GetRecentCommands(int count);
    IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count);
    void AddAuditEvent(AuditEvent auditEvent);
}

public interface ISetupService
{
    SetupStatus GetStatus();
    Task<SetupStatus> SaveAsync(SetupRequest request, CancellationToken cancellationToken);
}

public sealed class DashboardService(
    IServiceStatusProvider serviceStatusProvider,
    ISystemStatsProvider systemStatsProvider,
    INewsProvider newsProvider,
    IAgentSnapshotStore agentSnapshotStore,
    IAgentCommandStore commandStore,
    IOptions<DashboardOptions> options) : IDashboardService
{
    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var servicesTask = serviceStatusProvider.GetServicesAsync(cancellationToken);
        var newsTask = newsProvider.GetNewsAsync(cancellationToken);
        var latestAgent = agentSnapshotStore.GetLatest(options.Value.DefaultAgentId);

        return new DashboardSnapshot(
            DateTimeOffset.UtcNow,
            MergeServices(await servicesTask, latestAgent?.Services),
            latestAgent?.System ?? systemStatsProvider.GetStats(),
            await newsTask,
            agentSnapshotStore.GetAll(),
            BuildNotifications(latestAgent?.System ?? systemStatsProvider.GetStats(), await servicesTask, agentSnapshotStore.GetAll()),
            commandStore.GetRecentAuditEvents(8));
    }

    private static IReadOnlyList<ServiceCard> MergeServices(
        IReadOnlyList<ServiceCard> configuredServices,
        IReadOnlyList<ServiceCard>? agentServices)
    {
        if (agentServices is null || agentServices.Count == 0)
        {
            return configuredServices;
        }

        var merged = configuredServices.ToDictionary(service => service.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var agentService in agentServices)
        {
            merged[agentService.Id] = agentService;
        }

        return merged.Values.OrderBy(service => service.Name).ToArray();
    }

    private static IReadOnlyList<DashboardNotification> BuildNotifications(
        SystemStats system,
        IReadOnlyList<ServiceCard> services,
        IReadOnlyList<AgentSummary> agents)
    {
        var notifications = new List<DashboardNotification>();
        var now = DateTimeOffset.UtcNow;

        foreach (var service in services.Where(service => service.Status is ServiceStatus.Offline or ServiceStatus.Degraded))
        {
            notifications.Add(new DashboardNotification(
                $"service-{service.Id}",
                service.Status == ServiceStatus.Offline ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                $"{service.Name} is {service.Status}",
                service.StatusMessage ?? "Service needs attention.",
                now));
        }

        foreach (var disk in system.Disks)
        {
            var usedPercent = disk.TotalBytes > 0 ? (double)(disk.TotalBytes - disk.FreeBytes) / disk.TotalBytes * 100 : 0;
            if (usedPercent >= 90)
            {
                notifications.Add(new DashboardNotification(
                    $"disk-{disk.Name}",
                    NotificationSeverity.Critical,
                    $"Disk {disk.Name} is almost full",
                    $"{usedPercent:0}% used.",
                    now));
            }
        }

        foreach (var agent in agents.Where(agent => agent.Status != ServiceStatus.Online))
        {
            notifications.Add(new DashboardNotification(
                $"agent-{agent.AgentId}",
                NotificationSeverity.Warning,
                $"Agent {agent.AgentId} is stale",
                $"Last seen {agent.LastSeenAt.LocalDateTime}.",
                now));
        }

        return notifications.Take(12).ToArray();
    }
}

public sealed class FileDashboardStateStore : IAgentSnapshotStore, IAgentCommandStore
{
    private readonly object gate = new();
    private readonly JsonSerializerOptions serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string statePath;
    private readonly int historyLimit;
    private DashboardState state;

    public FileDashboardStateStore(IOptions<DashboardOptions> options)
    {
        statePath = Path.GetFullPath(options.Value.DataPath, AppContext.BaseDirectory);
        historyLimit = Math.Max(options.Value.AgentHistoryLimit, 10);
        state = Load();
    }

    public AgentSnapshot? GetLatest(string agentId)
    {
        lock (gate)
        {
            return state.Snapshots.GetValueOrDefault(agentId);
        }
    }

    public IReadOnlyList<AgentSummary> GetAll()
    {
        lock (gate)
        {
            return state.Snapshots.Values
                .OrderBy(snapshot => snapshot.AgentId)
                .Select(snapshot => new AgentSummary(
                    snapshot.AgentId,
                    snapshot.System.Hostname,
                    snapshot.CapturedAt,
                    snapshot.CapturedAt < DateTimeOffset.UtcNow.AddMinutes(-2) ? ServiceStatus.Degraded : ServiceStatus.Online,
                    snapshot.Services.Count))
                .ToArray();
        }
    }

    public IReadOnlyList<AgentHistoryPoint> GetHistory(string agentId)
    {
        lock (gate)
        {
            return state.History.TryGetValue(agentId, out var history) ? history.ToArray() : [];
        }
    }

    public void Save(AgentSnapshot snapshot)
    {
        lock (gate)
        {
            state.Snapshots[snapshot.AgentId] = snapshot;
            if (!state.History.TryGetValue(snapshot.AgentId, out var history))
            {
                history = [];
                state.History[snapshot.AgentId] = history;
            }

            history.Add(new AgentHistoryPoint(
                snapshot.AgentId,
                snapshot.CapturedAt,
                snapshot.System.CpuPercent,
                snapshot.System.MemoryUsedPercent,
                snapshot.Services.Count(service => service.Status == ServiceStatus.Online),
                snapshot.Services.Count(service => service.Status == ServiceStatus.Degraded),
                snapshot.Services.Count(service => service.Status == ServiceStatus.Offline)));

            if (history.Count > historyLimit)
            {
                history.RemoveRange(0, history.Count - historyLimit);
            }

            AddAuditEventLocked(new AuditEvent(
                Guid.NewGuid().ToString("n"),
                AuditEventType.AgentSnapshotReceived,
                $"Agent {snapshot.AgentId} reported {snapshot.Services.Count} service(s).",
                null,
                snapshot.AgentId,
                "agent",
                DateTimeOffset.UtcNow));
            SaveLocked();
        }
    }

    public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request)
    {
        var command = new AgentCommand(
            Guid.NewGuid().ToString("n"),
            agentId,
            AgentCommandKind.RestartService,
            serviceId,
            request.RequestedBy,
            request.Reason,
            DateTimeOffset.UtcNow,
            AgentCommandState.Queued);

        lock (gate)
        {
            state.Commands.Add(command);
            AddAuditEventLocked(new AuditEvent(
                Guid.NewGuid().ToString("n"),
                AuditEventType.RestartQueued,
                $"Restart queued for {serviceId} on agent {agentId}.",
                serviceId,
                agentId,
                request.RequestedBy,
                command.RequestedAt,
                command.Id));
            SaveLocked();
        }

        return command;
    }

    public AgentCommand? DequeueNext(string agentId)
    {
        lock (gate)
        {
            var command = state.Commands
                .Where(candidate => candidate.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                    && candidate.State == AgentCommandState.Queued)
                .OrderBy(candidate => candidate.RequestedAt)
                .FirstOrDefault();

            if (command is null)
            {
                return null;
            }

            var running = command with { State = AgentCommandState.Running, Message = "Agent is running the command." };
            ReplaceCommand(command.Id, running);
            SaveLocked();
            return running;
        }
    }

    public void Complete(string agentId, string commandId, AgentCommandCompletion completion)
    {
        lock (gate)
        {
            var command = state.Commands.FirstOrDefault(candidate =>
                candidate.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase)
                && candidate.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase));
            if (command is null)
            {
                return;
            }

            ReplaceCommand(command.Id, command with
            {
                State = completion.Succeeded ? AgentCommandState.Succeeded : AgentCommandState.Failed,
                Message = completion.Message,
                CompletedAt = DateTimeOffset.UtcNow
            });
            AddAuditEventLocked(new AuditEvent(
                Guid.NewGuid().ToString("n"),
                AuditEventType.RestartCompleted,
                completion.Message,
                command.ServiceId,
                agentId,
                "agent",
                DateTimeOffset.UtcNow,
                command.Id,
                completion.Succeeded));
            SaveLocked();
        }
    }

    public IReadOnlyList<AgentCommand> GetRecentCommands(int count)
    {
        lock (gate)
        {
            return state.Commands
                .OrderByDescending(command => command.RequestedAt)
                .Take(count)
                .ToArray();
        }
    }

    public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count)
    {
        lock (gate)
        {
            return state.AuditEvents
                .OrderByDescending(auditEvent => auditEvent.OccurredAt)
                .Take(count)
                .ToArray();
        }
    }

    public void AddAuditEvent(AuditEvent auditEvent)
    {
        lock (gate)
        {
            AddAuditEventLocked(auditEvent);
            SaveLocked();
        }
    }

    private DashboardState Load()
    {
        if (!File.Exists(statePath))
        {
            return new DashboardState();
        }

        try
        {
            var json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<DashboardState>(json, serializerOptions) ?? new DashboardState();
        }
        catch (JsonException)
        {
            return new DashboardState();
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var json = JsonSerializer.Serialize(state, serializerOptions);
        File.WriteAllText(statePath, json);
    }

    private void ReplaceCommand(string commandId, AgentCommand replacement)
    {
        var index = state.Commands.FindIndex(command => command.Id.Equals(commandId, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            state.Commands[index] = replacement;
        }
    }

    private void AddAuditEventLocked(AuditEvent auditEvent)
    {
        state.AuditEvents.Add(auditEvent);
        if (state.AuditEvents.Count > 300)
        {
            state.AuditEvents.RemoveRange(0, state.AuditEvents.Count - 300);
        }
    }

    private sealed class DashboardState
    {
        public Dictionary<string, AgentSnapshot> Snapshots { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<AgentHistoryPoint>> History { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<AgentCommand> Commands { get; init; } = [];
        public List<AuditEvent> AuditEvents { get; init; } = [];
    }
}

public sealed class ConfiguredServiceStatusProvider(
    IOptions<DashboardOptions> options,
    IHttpClientFactory httpClientFactory) : IServiceStatusProvider
{
    public async Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("health-checks");
        var checks = options.Value.Services.Select(service => CheckServiceAsync(client, service, cancellationToken));
        return await Task.WhenAll(checks);
    }

    private static async Task<ServiceCard> CheckServiceAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        if (service.Url is not null)
        {
            var integrationCard = await TryCheckIntegrationAsync(client, service, cancellationToken);
            if (integrationCard is not null)
            {
                return integrationCard;
            }
        }

        if (service.HealthUrl is null)
        {
            return ToCard(service, ServiceStatus.Unknown, "No health check configured.", []);
        }

        try
        {
            using var response = await client.GetAsync(service.HealthUrl, cancellationToken);
            var status = response.IsSuccessStatusCode ? ServiceStatus.Online : ToStatus(response.StatusCode);
            return ToCard(service, status, $"Health check returned {(int)response.StatusCode}.", []);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToCard(service, ServiceStatus.Degraded, "Health check timed out.", []);
        }
        catch (HttpRequestException ex)
        {
            return ToCard(service, ServiceStatus.Offline, ex.Message, []);
        }
    }

    private static async Task<ServiceCard?> TryCheckIntegrationAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        return service.Kind switch
        {
            ServiceKind.Plex => await CheckPlexAsync(client, service, cancellationToken),
            ServiceKind.Sonarr or ServiceKind.Radarr or ServiceKind.Lidarr or ServiceKind.Readarr or ServiceKind.Prowlarr
                => await CheckArrAsync(client, service, cancellationToken),
            ServiceKind.qBittorrent => await CheckQbittorrentAsync(client, service, cancellationToken),
            ServiceKind.SABnzbd => await CheckSabnzbdAsync(client, service, cancellationToken),
            ServiceKind.Jellyfin => await CheckJellyfinAsync(client, service, cancellationToken),
            _ => null
        };
    }

    private static async Task<ServiceCard> CheckPlexAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        var uri = BuildUri(service.Url!, "/identity", string.IsNullOrWhiteSpace(service.ApiKey) ? null : $"X-Plex-Token={Uri.EscapeDataString(service.ApiKey)}");

        try
        {
            using var response = await client.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ToCard(service, ToStatus(response.StatusCode), $"Plex identity returned {(int)response.StatusCode}.", []);
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xml);
            var root = document.Root;
            var version = root?.Attribute("version")?.Value;
            var machine = root?.Attribute("machineIdentifier")?.Value;
            var metrics = new List<ServiceMetric>();
            AddMetric(metrics, "Version", version);
            AddMetric(metrics, "Machine", Shorten(machine, 8));
            await AddPlexSessionsAsync(client, service, metrics, cancellationToken);
            return ToCard(service, ServiceStatus.Online, "Plex server API responded.", metrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return ToCard(service, ServiceStatus.Offline, $"Plex check failed: {ex.Message}", []);
        }
    }

    private static async Task<ServiceCard?> CheckArrAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.ApiKey))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(service.Url!, "/api/v3/system/status"));
        request.Headers.Add("X-Api-Key", service.ApiKey);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ToCard(service, ToStatus(response.StatusCode), $"{service.Kind} status returned {(int)response.StatusCode}.", []);
            }

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            var root = document!.RootElement;
            var version = ReadString(root, "version");
            var appName = ReadString(root, "appName") ?? service.Kind.ToString();
            var os = ReadString(root, "osName");
            var metrics = new List<ServiceMetric>();
            AddMetric(metrics, "Version", version);
            AddMetric(metrics, "OS", os);
            var healthIssues = await CountJsonArrayAsync(client, service, "/api/v3/health", cancellationToken);
            if (healthIssues is not null)
            {
                AddMetric(metrics, "Health Issues", healthIssues.Value.ToString());
            }

            var queueCount = await ReadJsonIntAsync(client, service, "/api/v3/queue/status", "totalCount", cancellationToken);
            if (queueCount is not null)
            {
                AddMetric(metrics, "Queue", queueCount.Value.ToString());
            }

            var status = healthIssues > 0 ? ServiceStatus.Degraded : ServiceStatus.Online;
            var message = healthIssues > 0 ? $"{appName} reported {healthIssues} health issue(s)." : $"{appName} API responded.";
            return ToCard(service, status, message, metrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ToCard(service, ServiceStatus.Offline, $"{service.Kind} check failed: {ex.Message}", []);
        }
    }

    private static async Task<ServiceCard> CheckQbittorrentAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = BuildUri(service.Url!, "/api/v2/app/version");
            var version = await client.GetStringAsync(uri, cancellationToken);
            var metrics = new List<ServiceMetric> { new("Version", version.Trim()) };
            await AddQbittorrentTransferAsync(client, service, metrics, cancellationToken);
            return ToCard(
                service,
                ServiceStatus.Online,
                "qBittorrent Web API responded.",
                metrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return ToCard(service, ServiceStatus.Offline, $"qBittorrent check failed: {ex.Message}", []);
        }
    }

    private static async Task<ServiceCard> CheckSabnzbdAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        var query = "mode=version&output=json";
        if (!string.IsNullOrWhiteSpace(service.ApiKey))
        {
            query += $"&apikey={Uri.EscapeDataString(service.ApiKey)}";
        }

        try
        {
            var uri = BuildUri(service.Url!, "/api", query);
            var document = await client.GetFromJsonAsync<JsonDocument>(uri, cancellationToken);
            var version = ReadString(document!.RootElement, "version");
            var metrics = new List<ServiceMetric>();
            AddMetric(metrics, "Version", version);
            await AddSabnzbdQueueAsync(client, service, metrics, cancellationToken);
            return ToCard(
                service,
                ServiceStatus.Online,
                "SABnzbd API responded.",
                metrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ToCard(service, ServiceStatus.Offline, $"SABnzbd check failed: {ex.Message}", []);
        }
    }

    private static async Task<ServiceCard> CheckJellyfinAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = BuildUri(service.Url!, "/System/Info/Public");
            var document = await client.GetFromJsonAsync<JsonDocument>(uri, cancellationToken);
            var root = document!.RootElement;
            var version = ReadString(root, "Version");
            var serverName = ReadString(root, "ServerName");
            var metrics = new List<ServiceMetric>();
            AddMetric(metrics, "Version", version);
            AddMetric(metrics, "Server", serverName);
            await AddJellyfinSessionsAsync(client, service, metrics, cancellationToken);
            return ToCard(service, ServiceStatus.Online, "Jellyfin public system API responded.", metrics);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return ToCard(service, ServiceStatus.Offline, $"Jellyfin check failed: {ex.Message}", []);
        }
    }

    private static Uri BuildUri(Uri baseUri, string path, string? query = null)
    {
        var builder = new UriBuilder(new Uri(baseUri, path));
        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.Query = query;
        }

        return builder.Uri;
    }

    private static async Task AddPlexSessionsAsync(
        HttpClient client,
        ServiceDefinition service,
        ICollection<ServiceMetric> metrics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.ApiKey))
        {
            return;
        }

        try
        {
            var uri = BuildUri(service.Url!, "/status/sessions", $"X-Plex-Token={Uri.EscapeDataString(service.ApiKey)}");
            var xml = await client.GetStringAsync(uri, cancellationToken);
            var count = XDocument.Parse(xml).Root?.Attribute("size")?.Value;
            AddMetric(metrics, "Streams", count);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
        }
    }

    private static async Task AddQbittorrentTransferAsync(
        HttpClient client,
        ServiceDefinition service,
        ICollection<ServiceMetric> metrics,
        CancellationToken cancellationToken)
    {
        try
        {
            var document = await client.GetFromJsonAsync<JsonDocument>(BuildUri(service.Url!, "/api/v2/transfer/info"), cancellationToken);
            var root = document!.RootElement;
            AddMetric(metrics, "Down", FormatBytesPerSecond(ReadLong(root, "dl_info_speed")));
            AddMetric(metrics, "Up", FormatBytesPerSecond(ReadLong(root, "up_info_speed")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
    }

    private static async Task AddSabnzbdQueueAsync(
        HttpClient client,
        ServiceDefinition service,
        ICollection<ServiceMetric> metrics,
        CancellationToken cancellationToken)
    {
        var query = "mode=queue&output=json";
        if (!string.IsNullOrWhiteSpace(service.ApiKey))
        {
            query += $"&apikey={Uri.EscapeDataString(service.ApiKey)}";
        }

        try
        {
            var document = await client.GetFromJsonAsync<JsonDocument>(BuildUri(service.Url!, "/api", query), cancellationToken);
            if (document!.RootElement.TryGetProperty("queue", out var queue))
            {
                AddMetric(metrics, "Queue", ReadString(queue, "noofslots"));
                AddMetric(metrics, "Speed", ReadString(queue, "kbpersec") is { } speed ? $"{speed} KB/s" : null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
    }

    private static async Task AddJellyfinSessionsAsync(
        HttpClient client,
        ServiceDefinition service,
        ICollection<ServiceMetric> metrics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(service.ApiKey))
        {
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(service.Url!, "/Sessions"));
            request.Headers.Add("X-Emby-Token", service.ApiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            AddMetric(metrics, "Sessions", document!.RootElement.GetArrayLength().ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
        }
    }

    private static async Task<int?> CountJsonArrayAsync(
        HttpClient client,
        ServiceDefinition service,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(service.Url!, path));
            request.Headers.Add("X-Api-Key", service.ApiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            return document!.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static async Task<int?> ReadJsonIntAsync(
        HttpClient client,
        ServiceDefinition service,
        string path,
        string propertyName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(service.Url!, path));
            request.Headers.Add("X-Api-Key", service.ApiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
            return ReadInt(document!.RootElement, propertyName);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static ServiceCard ToCard(
        ServiceDefinition service,
        ServiceStatus status,
        string message,
        IReadOnlyList<ServiceMetric> metrics)
        => new(
            service.Id,
            service.Name,
            service.Kind,
            service.Description,
            service.Url,
            status,
            service.RestartEnabled,
            DateTimeOffset.UtcNow,
            message,
            metrics);

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int? ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : null;

    private static long? ReadLong(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static void AddMetric(ICollection<ServiceMetric> metrics, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metrics.Add(new ServiceMetric(label, value));
        }
    }

    private static string? Shorten(string? value, int length)
        => string.IsNullOrWhiteSpace(value) || value.Length <= length ? value : value[..length];

    private static string? FormatBytesPerSecond(long? bytesPerSecond)
    {
        if (bytesPerSecond is null)
        {
            return null;
        }

        var units = new[] { "B/s", "KB/s", "MB/s", "GB/s" };
        var value = (double)bytesPerSecond.Value;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }

    private static ServiceStatus ToStatus(HttpStatusCode statusCode)
        => (int)statusCode >= 500 ? ServiceStatus.Degraded : ServiceStatus.Offline;
}

public sealed class LocalSystemStatsProvider : ISystemStatsProvider
{
    private DateTimeOffset lastSampledAt = DateTimeOffset.UtcNow;
    private TimeSpan lastProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
    private readonly PerformanceCounter? cpuCounter = CreateCpuCounter();

    public SystemStats GetStats()
    {
        var disks = DriveInfo
            .GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DiskStats(drive.Name, drive.TotalSize, drive.AvailableFreeSpace))
            .ToArray();

        var memoryUsed = GC.GetGCMemoryInfo();
        var memoryPercent = memoryUsed.TotalAvailableMemoryBytes > 0
            ? Math.Clamp((double)Process.GetCurrentProcess().WorkingSet64 / memoryUsed.TotalAvailableMemoryBytes * 100, 0, 100)
            : 0;

        return new SystemStats(
            Environment.MachineName,
            (OperatingSystem.IsWindows() ? GetHostCpuPercent() : null) ?? GetProcessCpuPercent(),
            Math.Round(GetMemoryUsedPercent(memoryPercent), 1),
            disks,
            DateTimeOffset.UtcNow);
    }

    [SupportedOSPlatform("windows")]
    private double? GetHostCpuPercent()
    {
        if (cpuCounter is null)
        {
            return null;
        }

        try
        {
            return Math.Round(Math.Clamp(cpuCounter.NextValue(), 0, 100), 1);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private double GetProcessCpuPercent()
    {
        var process = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        var processorTime = process.TotalProcessorTime;
        var elapsed = (now - lastSampledAt).TotalMilliseconds;
        var used = (processorTime - lastProcessorTime).TotalMilliseconds;
        lastSampledAt = now;
        lastProcessorTime = processorTime;

        if (elapsed <= 0)
        {
            return 0;
        }

        return Math.Round(Math.Clamp(used / (elapsed * Environment.ProcessorCount) * 100, 0, 100), 1);
    }

    private static double GetMemoryUsedPercent(double fallback)
    {
        if (!OperatingSystem.IsWindows())
        {
            return fallback;
        }

        var status = new MemoryStatusEx();
        return GlobalMemoryStatusEx(status) ? status.MemoryLoad : fallback;
    }

    private static PerformanceCounter? CreateCpuCounter()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            counter.NextValue();
            return counter;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

public sealed class RssNewsProvider(
    IOptions<DashboardOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<RssNewsProvider> logger) : INewsProvider
{
    public async Task<IReadOnlyList<NewsItem>> GetNewsAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("news");
        var items = new List<NewsItem>();

        foreach (var feed in options.Value.NewsFeeds)
        {
            try
            {
                await using var stream = await client.GetStreamAsync(feed.Url, cancellationToken);
                var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
                items.AddRange(ParseFeed(feed.Name, document));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
                logger.LogWarning(ex, "Failed to read news feed {FeedName}", feed.Name);
            }
        }

        return items
            .OrderByDescending(item => item.PublishedAt ?? DateTimeOffset.MinValue)
            .Take(12)
            .ToArray();
    }

    internal static IReadOnlyList<NewsItem> ParseFeed(string source, XDocument document)
    {
        var rssItems = document.Descendants("item").Select(item => new NewsItem(
            source,
            Read(item, "title") ?? "Untitled",
            TryUri(Read(item, "link")),
            TryDate(Read(item, "pubDate")),
            Read(item, "description")));

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var atomItems = document.Descendants(atom + "entry").Select(entry => new NewsItem(
            source,
            Read(entry, atom + "title") ?? "Untitled",
            TryUri(entry.Elements(atom + "link").FirstOrDefault()?.Attribute("href")?.Value),
            TryDate(Read(entry, atom + "updated") ?? Read(entry, atom + "published")),
            Read(entry, atom + "summary")));

        return rssItems.Concat(atomItems).ToArray();
    }

    private static string? Read(XElement element, XName name)
        => element.Element(name)?.Value.Trim();

    private static Uri? TryUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static DateTimeOffset? TryDate(string? value)
        => DateTimeOffset.TryParse(value, out var date) ? date : null;
}

public sealed class RestartCoordinator(
    IOptions<DashboardOptions> options,
    IAgentCommandStore commandStore) : IRestartCoordinator
{
    public RestartResult QueueRestart(string serviceId, RestartRequest request)
    {
        var service = options.Value.Services.FirstOrDefault(candidate => candidate.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            AddRestartRejection(serviceId, request.RequestedBy, "Service is not configured.");
            return new RestartResult(serviceId, RestartState.Rejected, "Service is not configured.", DateTimeOffset.UtcNow);
        }

        if (!service.RestartEnabled)
        {
            AddRestartRejection(serviceId, request.RequestedBy, "Restart controls are disabled for this service.");
            return new RestartResult(serviceId, RestartState.Unsupported, "Restart controls are disabled for this service.", DateTimeOffset.UtcNow);
        }

        if (!request.Confirmed)
        {
            AddRestartRejection(serviceId, request.RequestedBy, "Restart was not confirmed.");
            return new RestartResult(serviceId, RestartState.Rejected, "Restart requires confirmation.", DateTimeOffset.UtcNow);
        }

        var command = commandStore.Enqueue(options.Value.DefaultAgentId, serviceId, request);
        return new RestartResult(
            serviceId,
            RestartState.Queued,
            $"Restart queued for agent {command.AgentId}.",
            command.RequestedAt,
            command.Id);
    }

    private void AddRestartRejection(string serviceId, string actor, string message)
        => commandStore.AddAuditEvent(new AuditEvent(
            Guid.NewGuid().ToString("n"),
            AuditEventType.RestartRejected,
            message,
            serviceId,
            options.Value.DefaultAgentId,
            actor,
            DateTimeOffset.UtcNow,
            null,
            false));
}
