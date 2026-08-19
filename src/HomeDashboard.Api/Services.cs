using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
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
    void Save(AgentSnapshot snapshot);
}

public sealed class DashboardService(
    IServiceStatusProvider serviceStatusProvider,
    ISystemStatsProvider systemStatsProvider,
    INewsProvider newsProvider,
    IAgentSnapshotStore agentSnapshotStore,
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
            await newsTask);
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
}

public sealed class InMemoryAgentSnapshotStore : IAgentSnapshotStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, AgentSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    public AgentSnapshot? GetLatest(string agentId)
    {
        lock (gate)
        {
            return snapshots.GetValueOrDefault(agentId);
        }
    }

    public void Save(AgentSnapshot snapshot)
    {
        lock (gate)
        {
            snapshots[snapshot.AgentId] = snapshot;
        }
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
            return ToCard(service, ServiceStatus.Online, "Plex server identity responded.", metrics);
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
            return ToCard(service, ServiceStatus.Online, $"{appName} API responded.", metrics);
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
            return ToCard(
                service,
                ServiceStatus.Online,
                "qBittorrent Web API responded.",
                [new ServiceMetric("Version", version.Trim())]);
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
            return ToCard(
                service,
                ServiceStatus.Online,
                "SABnzbd API responded.",
                string.IsNullOrWhiteSpace(version) ? [] : [new ServiceMetric("Version", version)]);
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

    private static void AddMetric(ICollection<ServiceMetric> metrics, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metrics.Add(new ServiceMetric(label, value));
        }
    }

    private static string? Shorten(string? value, int length)
        => string.IsNullOrWhiteSpace(value) || value.Length <= length ? value : value[..length];

    private static ServiceStatus ToStatus(HttpStatusCode statusCode)
        => (int)statusCode >= 500 ? ServiceStatus.Degraded : ServiceStatus.Offline;
}

public sealed class LocalSystemStatsProvider : ISystemStatsProvider
{
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
            0,
            Math.Round(memoryPercent, 1),
            disks,
            DateTimeOffset.UtcNow);
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

public sealed class RestartCoordinator(IOptions<DashboardOptions> options) : IRestartCoordinator
{
    public RestartResult QueueRestart(string serviceId, RestartRequest request)
    {
        var service = options.Value.Services.FirstOrDefault(candidate => candidate.Id == serviceId);
        if (service is null)
        {
            return new RestartResult(serviceId, RestartState.Rejected, "Service is not configured.", DateTimeOffset.UtcNow);
        }

        if (!service.RestartEnabled)
        {
            return new RestartResult(serviceId, RestartState.Unsupported, "Restart controls are disabled for this service.", DateTimeOffset.UtcNow);
        }

        return new RestartResult(serviceId, RestartState.Queued, $"Restart requested by {request.RequestedBy}.", DateTimeOffset.UtcNow);
    }
}
