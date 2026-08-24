using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public sealed record ArrMediaLookupResult(
    string SelectionId,
    string Title,
    int? Year,
    string MediaType,
    string Source,
    string? ImdbId,
    int? TmdbId,
    int? TvdbId,
    string? Overview,
    string? ArtworkUrl);

public sealed record ArrMediaRequestResult(
    bool Succeeded,
    string Message,
    ArrMediaLookupResult? Media = null,
    string Status = "Requested");

public interface IArrMediaRequestService
{
    Task<IReadOnlyList<ArrMediaLookupResult>> SearchAsync(string query, CancellationToken cancellationToken);
    Task<ArrMediaRequestResult> RequestAsync(string selectionId, bool searchNow, CancellationToken cancellationToken);
}

public sealed class ArrMediaRequestService(
    IOptions<DashboardOptions> options,
    IHttpClientFactory httpClientFactory,
    ILogger<ArrMediaRequestService> logger) : IArrMediaRequestService
{
    private sealed record CachedLookup(ArrMediaLookupResult Result, ServiceDefinition Service, JsonObject Payload, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CachedLookup> lookups = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<ArrMediaLookupResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var term = query.Trim();
        if (term.Length < 2) return [];
        var services = options.Value.Services.Where(service => service.Url is not null && !string.IsNullOrWhiteSpace(service.ApiKey)
            && service.Kind is ServiceKind.Radarr or ServiceKind.Sonarr).ToArray();
        if (services.Length == 0) return [];

        var client = httpClientFactory.CreateClient("operations");
        var results = await Task.WhenAll(services.Select(service => SearchServiceAsync(client, service, term, cancellationToken)));
        var now = DateTimeOffset.UtcNow;
        foreach (var stale in lookups.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).Take(100)) lookups.TryRemove(stale, out _);
        return results.SelectMany(item => item)
            .OrderByDescending(item => ExactRank(item, term))
            .ThenByDescending(item => item.Year)
            .Take(20)
            .ToArray();
    }

    public async Task<ArrMediaRequestResult> RequestAsync(string selectionId, bool searchNow, CancellationToken cancellationToken)
    {
        if (!lookups.TryGetValue(selectionId, out var selected) || selected.ExpiresAt <= DateTimeOffset.UtcNow)
            return new(false, "That search result expired. Search for the title again and select a current result.", Status: "Not submitted");

        try
        {
            var client = httpClientFactory.CreateClient("operations");
            var rootTask = GetArrayAsync(client, selected.Service, "/api/v3/rootfolder", cancellationToken);
            var qualityTask = GetArrayAsync(client, selected.Service, "/api/v3/qualityprofile", cancellationToken);
            await Task.WhenAll(rootTask, qualityTask);
            using var roots = await rootTask;
            using var profiles = await qualityTask;
            var rootPath = SelectRootPath(roots);
            var qualityProfileId = SelectProfileId(profiles);
            if (rootPath is null || qualityProfileId is null)
                return new(false, $"{selected.Service.Name} needs at least one root folder and quality profile before Discord can add media.", selected.Result, "Not submitted");

            var payload = (JsonObject)selected.Payload.DeepClone();
            payload.Remove("id");
            payload["qualityProfileId"] = qualityProfileId.Value;
            payload["rootFolderPath"] = rootPath;
            payload["monitored"] = true;
            if (selected.Service.Kind == ServiceKind.Radarr)
            {
                payload["addOptions"] = new JsonObject { ["searchForMovie"] = searchNow };
            }
            else
            {
                payload["seasonFolder"] = true;
                payload["addOptions"] = new JsonObject
                {
                    ["monitor"] = "all",
                    ["searchForMissingEpisodes"] = searchNow,
                    ["searchForCutoffUnmetEpisodes"] = false
                };
            }

            using var request = CreateRequest(HttpMethod.Post, selected.Service,
                selected.Service.Kind == ServiceKind.Radarr ? "/api/v3/movie" : "/api/v3/series");
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = Limit(await response.Content.ReadAsStringAsync(cancellationToken), 500);
                return new(false, $"{selected.Service.Name} rejected the request with HTTP {(int)response.StatusCode}{(detail.Length == 0 ? "." : $": {detail}")}",
                    selected.Result, "Rejected");
            }

            lookups.TryRemove(selectionId, out _);
            var action = searchNow ? "added and a search was started" : "added without starting a search";
            return new(true, $"{selected.Result.Title}{Year(selected.Result.Year)} was {action} in {selected.Service.Name}.", selected.Result, "Submitted");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Media request failed for {SelectionId}", selectionId);
            return new(false, $"{selected.Service.Name} could not process the request: {ex.Message}", selected.Result, "Failed");
        }
    }

    private async Task<IReadOnlyList<ArrMediaLookupResult>> SearchServiceAsync(HttpClient client, ServiceDefinition service,
        string query, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = service.Kind == ServiceKind.Radarr ? "/api/v3/movie/lookup" : "/api/v3/series/lookup";
            using var request = CreateRequest(HttpMethod.Get, service, $"{endpoint}?term={Uri.EscapeDataString(query)}");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return [];
            using var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            if (document is null || document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var results = new List<ArrMediaLookupResult>();
            foreach (var item in document.RootElement.EnumerateArray().Take(20))
            {
                if (JsonNode.Parse(item.GetRawText()) is not JsonObject payload) continue;
                var result = ToResult(service, item);
                if (result is null) continue;
                lookups[result.SelectionId] = new CachedLookup(result, service, payload, DateTimeOffset.UtcNow.AddMinutes(10));
                results.Add(result);
            }
            return results;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Media lookup failed for {ServiceName}", service.Name);
            return [];
        }
    }

    private static ArrMediaLookupResult? ToResult(ServiceDefinition service, JsonElement item)
    {
        var title = ReadString(item, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        var year = ReadInt(item, "year");
        var tmdbId = ReadInt(item, "tmdbId");
        var tvdbId = ReadInt(item, "tvdbId");
        var externalId = service.Kind == ServiceKind.Radarr ? tmdbId : tvdbId;
        if (externalId is null or <= 0) return null;
        var type = service.Kind == ServiceKind.Radarr ? "Movie" : "TV";
        var selectionId = $"{(service.Kind == ServiceKind.Radarr ? "movie" : "series")}:{service.Id}:{externalId}";
        return new(selectionId, title, year, type, service.Name, ReadString(item, "imdbId"), tmdbId, tvdbId,
            ReadString(item, "overview"), ReadArtwork(item));
    }

    private static async Task<JsonDocument> GetArrayAsync(HttpClient client, ServiceDefinition service, string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, service, path);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? JsonDocument.Parse("[]");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, ServiceDefinition service, string path)
    {
        var request = new HttpRequestMessage(method, BuildUri(service.Url!, path));
        request.Headers.Add("X-Api-Key", service.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static Uri BuildUri(Uri baseUri, string path)
    {
        var normalized = baseUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(baseUri) { Host = "127.0.0.1" }.Uri
            : baseUri;
        return new Uri(normalized, path);
    }

    private static string? SelectRootPath(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        return document.RootElement.EnumerateArray()
            .Where(item => !string.IsNullOrWhiteSpace(ReadString(item, "path")))
            .OrderByDescending(item => ReadLong(item, "freeSpace") ?? 0)
            .Select(item => ReadString(item, "path"))
            .FirstOrDefault();
    }

    private static int? SelectProfileId(JsonDocument document)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var profiles = document.RootElement.EnumerateArray().Select(item => new { Id = ReadInt(item, "id"), Name = ReadString(item, "name") }).Where(item => item.Id > 0).ToArray();
        return profiles.FirstOrDefault(item => item.Name?.Equals("Any", StringComparison.OrdinalIgnoreCase) == true)?.Id ?? profiles.FirstOrDefault()?.Id;
    }

    private static string? ReadArtwork(JsonElement item)
    {
        if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array) return null;
        var poster = images.EnumerateArray().FirstOrDefault(image => ReadString(image, "coverType")?.Equals("poster", StringComparison.OrdinalIgnoreCase) == true);
        return poster.ValueKind == JsonValueKind.Undefined ? null : ReadString(poster, "remoteUrl") ?? ReadString(poster, "url");
    }

    private static int ExactRank(ArrMediaLookupResult item, string query)
    {
        var normalized = query.Trim();
        if (item.ImdbId?.Equals(normalized.Replace("imdb:", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase) == true) return 4;
        if (item.Title.Equals(normalized, StringComparison.OrdinalIgnoreCase)) return 3;
        if ($"{item.Title} {item.Year}".Equals(normalized, StringComparison.OrdinalIgnoreCase)) return 2;
        return 1;
    }

    private static string? ReadString(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() : null;
    private static int? ReadInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;
    private static long? ReadLong(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : null;
    private static string Year(int? year) => year is null ? "" : $" ({year})";
    private static string Limit(string value, int maximum) => value.Length <= maximum ? value : value[..maximum];
}
