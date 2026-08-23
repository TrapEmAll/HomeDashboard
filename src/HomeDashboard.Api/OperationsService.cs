using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public interface IOperationsService
{
    Task<OperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<bool> ControlDownloadAsync(DownloadControlRequest request, CancellationToken cancellationToken);
    IReadOnlyList<MaintenanceWindow> GetMaintenance();
    MaintenanceWindow AddMaintenance(CreateMaintenanceWindowRequest request, string actor);
    bool RemoveMaintenance(string id);
    void ReplaceMaintenance(IReadOnlyList<MaintenanceWindow> windows);
    Task<ServiceDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken);
}

public sealed class OperationsService(
    IOptions<DashboardOptions> options,
    IServiceStatusProvider serviceStatusProvider,
    IAgentCommandStore commandStore,
    IHttpClientFactory httpClientFactory,
    ILogger<OperationsService> logger) : IOperationsService
{
    private readonly string maintenancePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.DataPath, AppContext.BaseDirectory)) ?? AppContext.BaseDirectory, "homedashboard-maintenance.json");
    private readonly ConcurrentDictionary<string, MaintenanceWindow> maintenance = LoadMaintenance(
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.Value.DataPath, AppContext.BaseDirectory)) ?? AppContext.BaseDirectory, "homedashboard-maintenance.json"));
    private readonly SemaphoreSlim snapshotLock = new(1, 1);
    private OperationsSnapshot? cachedSnapshot;
    private DateTimeOffset snapshotExpiresAt = DateTimeOffset.MinValue;

    public async Task<OperationsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        if (cachedSnapshot is not null && DateTimeOffset.UtcNow < snapshotExpiresAt)
        {
            return cachedSnapshot;
        }

        await snapshotLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedSnapshot is not null && DateTimeOffset.UtcNow < snapshotExpiresAt)
            {
                return cachedSnapshot;
            }

            cachedSnapshot = await BuildSnapshotAsync(cancellationToken);
            snapshotExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10);
            return cachedSnapshot;
        }
        finally
        {
            snapshotLock.Release();
        }
    }

    private async Task<OperationsSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("operations");
        var servicesTask = GetServicesAsync(cancellationToken);
        var calendarTask = GetCalendarAsync(client, cancellationToken);
        var playbackTask = GetPlaybackAsync(client, cancellationToken);
        var downloadsTask = GetDownloadsAsync(client, cancellationToken);
        await Task.WhenAll(servicesTask, calendarTask, playbackTask, downloadsTask);

        var services = await servicesTask;
        var calendar = await calendarTask;
        var playback = await playbackTask;
        var downloads = await downloadsTask;
        var audit = commandStore.GetRecentAuditEvents(40);
        var incidents = services
            .Where(service => service.Status is ServiceStatus.Offline or ServiceStatus.Degraded)
            .Select(service => new IncidentSummary(
                $"service-{service.Id}",
                service.Id,
                service.Name,
                service.Status == ServiceStatus.Offline ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                service.StatusMessage ?? $"{service.Name} is {service.Status}.",
                service.LastCheckedAt ?? DateTimeOffset.UtcNow))
            .ToArray();

        var activity = audit.Select(item => new OperationsActivity(
                item.Id,
                item.OccurredAt,
                "HomeDashboard",
                item.Type.ToString(),
                item.Message,
                item.Type == AuditEventType.SetupSaved ? OperationsActivityKind.Security : OperationsActivityKind.Service,
                item.Succeeded ? NotificationSeverity.Info : NotificationSeverity.Warning))
            .Concat(playback.Select(item => new OperationsActivity(
                $"playback-{item.Id}", DateTimeOffset.UtcNow, "Plex", item.Title,
                $"{item.User} on {item.Player} - {item.ProgressPercent}%", OperationsActivityKind.Playback)))
            .Concat(downloads.Take(12).Select(item => new OperationsActivity(
                $"download-{item.Source}-{item.Id}", DateTimeOffset.UtcNow, item.Source, item.Name,
                $"{item.Status} - {item.ProgressPercent:0}%", OperationsActivityKind.Download)))
            .OrderByDescending(item => item.OccurredAt)
            .Take(60)
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        return new OperationsSnapshot(
            now,
            activity,
            calendar,
            playback,
            downloads,
            services.Select(service => new ServiceUptimeSummary(
                service.Id,
                service.Name,
                service.Status == ServiceStatus.Online ? 100 : service.Status == ServiceStatus.Degraded ? 99 : 0,
                now.AddDays(-7),
                service.Status is ServiceStatus.Online or ServiceStatus.Unknown ? 0 : 1,
                service.Status)).ToArray(),
            GetStorageForecasts(),
            incidents,
            GetMaintenance(),
            new UpdateSummary(
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0",
                "main",
                new Uri("https://github.com/TrapEmAll/HomeDashboard"),
                null,
                false,
                null));
    }

    public IReadOnlyList<MaintenanceWindow> GetMaintenance()
        => maintenance.Values.OrderBy(item => item.StartsAt).ToArray();

    public MaintenanceWindow AddMaintenance(CreateMaintenanceWindowRequest request, string actor)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.EndsAt <= request.StartsAt)
        {
            throw new InvalidOperationException("Maintenance needs a title and an end time after its start time.");
        }

        var window = new MaintenanceWindow(
            Guid.NewGuid().ToString("n"), request.Title.Trim(), request.StartsAt, request.EndsAt,
            string.IsNullOrWhiteSpace(request.ServiceId) ? null : request.ServiceId.Trim(), request.SuppressAlerts, actor);
        maintenance[window.Id] = window;
        snapshotExpiresAt = DateTimeOffset.MinValue;
        PersistMaintenance();
        return window;
    }

    public bool RemoveMaintenance(string id)
    {
        var removed = maintenance.TryRemove(id, out _);
        if (removed)
        {
            snapshotExpiresAt = DateTimeOffset.MinValue;
            PersistMaintenance();
        }
        return removed;
    }

    public void ReplaceMaintenance(IReadOnlyList<MaintenanceWindow> windows)
    {
        maintenance.Clear();
        foreach (var window in windows.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
        {
            maintenance[window.Id] = window;
        }
        snapshotExpiresAt = DateTimeOffset.MinValue;
        PersistMaintenance();
    }

    public async Task<ServiceDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var known = new (string Id, string Name, ServiceKind Kind, int Port)[]
        {
            ("plex", "Plex", ServiceKind.Plex, 32400),
            ("sonarr", "Sonarr", ServiceKind.Sonarr, 8989),
            ("radarr", "Radarr", ServiceKind.Radarr, 7878),
            ("prowlarr", "Prowlarr", ServiceKind.Prowlarr, 9696),
            ("lidarr", "Lidarr", ServiceKind.Lidarr, 8686),
            ("readarr", "Readarr", ServiceKind.Readarr, 8787),
            ("bazarr", "Bazarr", ServiceKind.Bazarr, 6767),
            ("sabnzbd", "SABnzbd", ServiceKind.SABnzbd, 8085),
            ("qbittorrent", "qBittorrent", ServiceKind.qBittorrent, 8080),
            ("jellyfin", "Jellyfin", ServiceKind.Jellyfin, 8096)
        };
        var configured = options.Value.Services.Select(service => service.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scans = known.Select(async candidate =>
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await tcp.ConnectAsync("127.0.0.1", candidate.Port, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                return new DiscoveredService(
                    candidate.Id, candidate.Name, candidate.Kind,
                    new Uri($"http://127.0.0.1:{candidate.Port}"), candidate.Port, configured.Contains(candidate.Id));
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException)
            {
                return null;
            }
        });
        return new ServiceDiscoveryResult((await Task.WhenAll(scans)).Where(item => item is not null).Cast<DiscoveredService>().ToArray(), DateTimeOffset.UtcNow);
    }

    public async Task<bool> ControlDownloadAsync(DownloadControlRequest request, CancellationToken cancellationToken)
    {
        var service = options.Value.Services.FirstOrDefault(candidate =>
            candidate.Kind == ServiceKind.qBittorrent && request.Source.Equals("qBittorrent", StringComparison.OrdinalIgnoreCase));
        if (service?.Url is null || string.IsNullOrWhiteSpace(request.ItemId))
        {
            return false;
        }

        var endpoint = request.Action switch
        {
            DownloadControlAction.Pause => "/api/v2/torrents/stop",
            DownloadControlAction.Resume => "/api/v2/torrents/start",
            DownloadControlAction.Recheck => "/api/v2/torrents/recheck",
            DownloadControlAction.Remove => "/api/v2/torrents/delete",
            _ => null
        };
        if (endpoint is null)
        {
            return false;
        }

        var values = new Dictionary<string, string> { ["hashes"] = request.ItemId };
        if (request.Action == DownloadControlAction.Remove)
        {
            values["deleteFiles"] = request.DeleteData.ToString().ToLowerInvariant();
        }

        try
        {
            var client = httpClientFactory.CreateClient("operations");
            using var response = await client.PostAsync(BuildUri(service.Url, endpoint), new FormUrlEncodedContent(values), cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound || request.Action is not (DownloadControlAction.Pause or DownloadControlAction.Resume))
            {
                if (response.IsSuccessStatusCode)
                {
                    snapshotExpiresAt = DateTimeOffset.MinValue;
                    return true;
                }
                return false;
            }

            var legacyEndpoint = request.Action == DownloadControlAction.Pause ? "/api/v2/torrents/pause" : "/api/v2/torrents/resume";
            using var legacyResponse = await client.PostAsync(BuildUri(service.Url, legacyEndpoint), new FormUrlEncodedContent(values), cancellationToken);
            if (legacyResponse.IsSuccessStatusCode)
            {
                snapshotExpiresAt = DateTimeOffset.MinValue;
            }
            return legacyResponse.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Download control request failed for {ItemId}", request.ItemId);
            return false;
        }
    }

    private async Task<IReadOnlyList<ServiceCard>> GetServicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await serviceStatusProvider.GetServicesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Operations service could not collect service health");
            return [];
        }
    }

    private async Task<IReadOnlyList<MediaCalendarItem>> GetCalendarAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var tasks = options.Value.Services
            .Where(service => service.Kind is ServiceKind.Sonarr or ServiceKind.Radarr && service.Url is not null && !string.IsNullOrWhiteSpace(service.ApiKey))
            .Select(service => GetServiceCalendarAsync(client, service, cancellationToken));
        return (await Task.WhenAll(tasks)).SelectMany(items => items).OrderBy(item => item.AirsAt).Take(100).ToArray();
    }

    private async Task<IReadOnlyList<MediaCalendarItem>> GetServiceCalendarAsync(HttpClient client, ServiceDefinition service, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var end = DateTimeOffset.UtcNow.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(service.Url!, $"/api/v3/calendar?start={start}&end={end}&includeUnmonitored=false"));
        request.Headers.Add("X-Api-Key", service.ApiKey);
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            return document?.RootElement.EnumerateArray().Select(item => ParseCalendarItem(service, item)).Where(item => item is not null).Cast<MediaCalendarItem>().ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Calendar query failed for {ServiceName}", service.Name);
            return [];
        }
    }

    private static MediaCalendarItem? ParseCalendarItem(ServiceDefinition service, JsonElement item)
    {
        var dateName = service.Kind == ServiceKind.Sonarr ? "airDateUtc" : "digitalRelease";
        if (!item.TryGetProperty(dateName, out var dateElement) && !item.TryGetProperty("inCinemas", out dateElement)
            || !DateTimeOffset.TryParse(dateElement.GetString(), out var date))
        {
            return null;
        }

        var id = item.TryGetProperty("id", out var idElement) ? idElement.ToString() : Guid.NewGuid().ToString("n");
        var title = item.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
        string? subtitle = null;
        if (service.Kind == ServiceKind.Sonarr && item.TryGetProperty("series", out var series) && series.TryGetProperty("title", out var seriesTitle))
        {
            subtitle = title;
            title = seriesTitle.GetString();
        }

        return new MediaCalendarItem(
            $"{service.Id}-{id}", service.Name, title ?? "Upcoming release", subtitle, date,
            service.Kind == ServiceKind.Sonarr ? "Episode" : "Movie",
            !item.TryGetProperty("monitored", out var monitored) || monitored.GetBoolean(),
            item.TryGetProperty("hasFile", out var hasFile) && hasFile.GetBoolean());
    }

    private async Task<IReadOnlyList<PlaybackSession>> GetPlaybackAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var plex = options.Value.Services.FirstOrDefault(service => service.Kind == ServiceKind.Plex && service.Url is not null && !string.IsNullOrWhiteSpace(service.ApiKey));
        if (plex?.Url is null)
        {
            return [];
        }

        try
        {
            var uri = BuildUri(plex.Url, $"/status/sessions?X-Plex-Token={Uri.EscapeDataString(plex.ApiKey!)}");
            var xml = XDocument.Parse(await client.GetStringAsync(uri, cancellationToken));
            return xml.Descendants().Where(element => element.Name.LocalName is "Video" or "Track").Select(ParsePlayback).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Plex session query failed");
            return [];
        }
    }

    private static PlaybackSession ParsePlayback(XElement item)
    {
        var duration = ReadLong(item.Attribute("duration")?.Value);
        var offset = ReadLong(item.Attribute("viewOffset")?.Value);
        var media = item.Elements().FirstOrDefault(element => element.Name.LocalName == "Media");
        var transcode = item.Elements().FirstOrDefault(element => element.Name.LocalName == "TranscodeSession");
        return new PlaybackSession(
            item.Attribute("sessionKey")?.Value ?? Guid.NewGuid().ToString("n"),
            item.Elements().FirstOrDefault(element => element.Name.LocalName == "User")?.Attribute("title")?.Value ?? "Unknown user",
            item.Attribute("grandparentTitle")?.Value ?? item.Attribute("title")?.Value ?? "Unknown title",
            item.Attribute("grandparentTitle") is null ? null : item.Attribute("title")?.Value,
            item.Elements().FirstOrDefault(element => element.Name.LocalName == "Player")?.Attribute("title")?.Value ?? "Unknown player",
            transcode?.Attribute("videoDecision")?.Value ?? transcode?.Attribute("audioDecision")?.Value ?? "direct play",
            duration > 0 ? (int)Math.Clamp(offset * 100 / duration, 0, 100) : 0,
            media?.Attribute("videoResolution")?.Value,
            ReadNullableLong(transcode?.Attribute("bandwidth")?.Value));
    }

    private async Task<IReadOnlyList<DownloadQueueItem>> GetDownloadsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var tasks = options.Value.Services.Where(service => service.Url is not null && service.Kind is ServiceKind.qBittorrent or ServiceKind.SABnzbd)
            .Select(service => service.Kind == ServiceKind.qBittorrent ? GetQbitDownloadsAsync(client, service, cancellationToken) : GetSabDownloadsAsync(client, service, cancellationToken));
        return (await Task.WhenAll(tasks)).SelectMany(items => items).OrderByDescending(item => item.DownloadSpeedBytes ?? 0).Take(80).ToArray();
    }

    private async Task<IReadOnlyList<DownloadQueueItem>> GetQbitDownloadsAsync(HttpClient client, ServiceDefinition service, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await client.GetFromJsonAsync<JsonDocument>(BuildUri(service.Url!, "/api/v2/torrents/info"), cancellationToken);
            return document?.RootElement.EnumerateArray().Select(item => new DownloadQueueItem(
                ReadJsonString(item, "hash") ?? Guid.NewGuid().ToString("n"), "qBittorrent", ReadJsonString(item, "name") ?? "Torrent",
                ReadJsonString(item, "state") ?? "unknown", Math.Round(ReadJsonDouble(item, "progress") * 100, 1),
                ReadJsonLong(item, "size"), ReadJsonLong(item, "amount_left"), ReadJsonLong(item, "dlspeed"),
                ReadJsonLong(item, "eta") is long eta && eta is > 0 and < 8_640_000 ? TimeSpan.FromSeconds(eta) : null,
                true, true)).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogDebug(ex, "qBittorrent queue query failed");
            return [];
        }
    }

    private async Task<IReadOnlyList<DownloadQueueItem>> GetSabDownloadsAsync(HttpClient client, ServiceDefinition service, CancellationToken cancellationToken)
    {
        try
        {
            var query = "/api?mode=queue&output=json" + (string.IsNullOrWhiteSpace(service.ApiKey) ? "" : $"&apikey={Uri.EscapeDataString(service.ApiKey)}");
            using var document = await client.GetFromJsonAsync<JsonDocument>(BuildUri(service.Url!, query), cancellationToken);
            if (document is null || !document.RootElement.TryGetProperty("queue", out var queue) || !queue.TryGetProperty("slots", out var slots))
            {
                return [];
            }

            return slots.EnumerateArray().Select(item => new DownloadQueueItem(
                ReadJsonString(item, "nzo_id") ?? Guid.NewGuid().ToString("n"), "SABnzbd", ReadJsonString(item, "filename") ?? "Download",
                ReadJsonString(item, "status") ?? "unknown", ReadJsonDouble(item, "percentage"),
                null, null, null, null, false, false)).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogDebug(ex, "SABnzbd queue query failed");
            return [];
        }
    }

    private static IReadOnlyList<StorageForecast> GetStorageForecasts()
        => DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive =>
        {
            var used = drive.TotalSize > 0 ? (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100 : 0;
            return new StorageForecast(drive.Name, drive.TotalSize, drive.AvailableFreeSpace, Math.Round(used, 1), null, null);
        }).ToArray();

    private static Uri BuildUri(Uri baseUri, string path)
    {
        var normalized = baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(baseUri) { Host = "127.0.0.1" }.Uri
            : baseUri;
        return new Uri(normalized, path);
    }

    private static long ReadLong(string? value) => long.TryParse(value, out var parsed) ? parsed : 0;
    private static long? ReadNullableLong(string? value) => long.TryParse(value, out var parsed) ? parsed : null;
    private static string? ReadJsonString(JsonElement item, string name) => item.TryGetProperty(name, out var value) ? value.ToString() : null;
    private static long? ReadJsonLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;
    private static double ReadJsonDouble(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetDouble(out var parsed) ? parsed : 0;

    private static ConcurrentDictionary<string, MaintenanceWindow> LoadMaintenance(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ConcurrentDictionary<string, MaintenanceWindow>(StringComparer.OrdinalIgnoreCase);
            }
            var items = JsonSerializer.Deserialize<MaintenanceWindow[]>(File.ReadAllText(path)) ?? [];
            return new ConcurrentDictionary<string, MaintenanceWindow>(items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new ConcurrentDictionary<string, MaintenanceWindow>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void PersistMaintenance()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(maintenancePath)!);
            File.WriteAllText(maintenancePath, JsonSerializer.Serialize(GetMaintenance()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Maintenance windows could not be persisted");
        }
    }
}
