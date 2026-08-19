using System.Diagnostics;
using System.Net;
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
        var services = new List<ServiceCard>();
        var client = httpClientFactory.CreateClient("health-checks");

        foreach (var service in options.Value.Services)
        {
            services.Add(await CheckServiceAsync(client, service, cancellationToken));
        }

        return services;
    }

    private static async Task<ServiceCard> CheckServiceAsync(
        HttpClient client,
        ServiceDefinition service,
        CancellationToken cancellationToken)
    {
        if (service.HealthUrl is null)
        {
            return ToCard(service, ServiceStatus.Unknown, "No health check configured.");
        }

        try
        {
            using var response = await client.GetAsync(service.HealthUrl, cancellationToken);
            var status = response.IsSuccessStatusCode ? ServiceStatus.Online : ToStatus(response.StatusCode);
            return ToCard(service, status, $"Health check returned {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToCard(service, ServiceStatus.Degraded, "Health check timed out.");
        }
        catch (HttpRequestException ex)
        {
            return ToCard(service, ServiceStatus.Offline, ex.Message);
        }
    }

    private static ServiceCard ToCard(ServiceDefinition service, ServiceStatus status, string message)
        => new(
            service.Id,
            service.Name,
            service.Description,
            service.Url,
            status,
            service.RestartEnabled,
            DateTimeOffset.UtcNow,
            message);

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
