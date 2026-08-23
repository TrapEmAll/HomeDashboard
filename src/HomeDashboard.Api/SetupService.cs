using System.Security.Cryptography;
using System.Text.Json;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public sealed class SetupService(
    IOptions<DashboardOptions> dashboardOptions,
    IOptions<DashboardSecurityOptions> securityOptions,
    IAgentCommandStore auditStore,
    ILocalSettingsWriter settingsWriter) : ISetupService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public SetupStatus GetStatus()
    {
        var security = securityOptions.Value;
        var dashboard = dashboardOptions.Value;
        var hasDashboardPasswordHash = !string.IsNullOrWhiteSpace(security.DashboardPasswordHash);
        var hasDashboardPassword = !IsPlaceholder(security.DashboardPassword);
        var usesPlaceholders = IsPlaceholder(security.DashboardApiKey)
            || IsPlaceholder(security.AgentApiKey)
            || !hasDashboardPasswordHash && !hasDashboardPassword;

        return new SetupStatus(
            !usesPlaceholders,
            usesPlaceholders,
            false,
            dashboard.DefaultAgentId,
            dashboard.Services.Count,
            dashboard.NewsFeeds.Count);
    }

    public async Task<SetupStatus> SaveAsync(SetupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DashboardPassword))
        {
            throw new InvalidOperationException("Dashboard password is required.");
        }

        var settings = new
        {
            Security = new
            {
                DashboardApiKey = UseOrGenerate(request.DashboardApiKey),
                AgentApiKey = UseOrGenerate(request.AgentApiKey),
                DashboardPassword = "",
                DashboardPasswordHash = ApiKeyValidator.HashSecret(request.DashboardPassword),
                securityOptions.Value.SessionCookieName,
                securityOptions.Value.SessionDuration
            },
            Dashboard = new
            {
                DefaultAgentId = string.IsNullOrWhiteSpace(request.DefaultAgentId) ? "server-pc" : request.DefaultAgentId,
                dashboardOptions.Value.DataPath,
                dashboardOptions.Value.AgentHistoryLimit,
                dashboardOptions.Value.IncludeRecommendedFeeds,
                Services = request.Services.Select(ToService).ToArray(),
                NewsFeeds = request.NewsFeeds.Select(ToFeed).ToArray()
            }
        };

        await settingsWriter.WriteAsync(JsonSerializer.Serialize(settings, SerializerOptions), cancellationToken);

        auditStore.AddAuditEvent(new AuditEvent(
            Guid.NewGuid().ToString("n"),
            AuditEventType.SetupSaved,
            "Setup configuration was saved. Restart the API to load all new values.",
            null,
            null,
            "setup",
            DateTimeOffset.UtcNow));

        return GetStatus() with { RequiresRestart = true };
    }

    public DashboardSettings GetSettings()
    {
        var dashboard = dashboardOptions.Value;
        return new DashboardSettings(
            dashboard.DefaultAgentId,
            dashboard.IncludeRecommendedFeeds,
            dashboard.Services.Select(service => new ServiceSetting(
                service.Id,
                service.Name,
                service.Kind,
                service.Description,
                service.Url?.ToString(),
                service.HealthUrl?.ToString(),
                !string.IsNullOrWhiteSpace(service.ApiKey),
                service.RestartEnabled)).ToArray(),
            dashboard.NewsFeeds.Where(feed => feed?.Url is not null).Select(feed => new NewsFeedSetting(
                feed.Name,
                feed.Url.ToString(),
                feed.Kind,
                feed.Category,
                feed.ProviderUrl?.ToString())).ToArray());
    }

    public async Task<DashboardSettings> UpdateSettingsAsync(
        UpdateDashboardSettingsRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSettings(request);

        var dashboard = dashboardOptions.Value;
        var security = securityOptions.Value;
        var existingServices = dashboard.Services.ToDictionary(service => service.Id, StringComparer.OrdinalIgnoreCase);
        var services = request.Services.Select(service =>
        {
            existingServices.TryGetValue(service.Id, out var existing);
            var apiKey = service.ClearApiKey
                ? null
                : string.IsNullOrWhiteSpace(service.ApiKey) ? existing?.ApiKey : service.ApiKey.Trim();

            return new
            {
                Id = service.Id.Trim(),
                Name = service.Name.Trim(),
                Kind = service.Kind.ToString(),
                Description = service.Description.Trim(),
                Url = NormalizeOptionalUri(service.Url, $"URL for {service.Name}"),
                HealthUrl = NormalizeOptionalUri(service.HealthUrl, $"health URL for {service.Name}"),
                ApiKey = apiKey,
                service.RestartEnabled
            };
        }).ToArray();

        var feeds = request.NewsFeeds.Select(feed => new
        {
            Name = feed.Name.Trim(),
            Url = NormalizeRequiredUri(feed.Url, $"feed URL for {feed.Name}"),
            Kind = feed.Kind.ToString(),
            Category = string.IsNullOrWhiteSpace(feed.Category) ? "Technology" : feed.Category.Trim(),
            ProviderUrl = NormalizeOptionalUri(feed.ProviderUrl, $"provider URL for {feed.Name}")
        }).ToArray();

        var settings = new
        {
            Security = new
            {
                security.DashboardApiKey,
                security.AgentApiKey,
                security.DashboardPassword,
                security.DashboardPasswordHash,
                security.SessionCookieName,
                security.SessionDuration
            },
            Dashboard = new
            {
                DefaultAgentId = request.DefaultAgentId.Trim(),
                dashboard.DataPath,
                dashboard.AgentHistoryLimit,
                request.IncludeRecommendedFeeds,
                Services = services,
                NewsFeeds = feeds
            }
        };

        await settingsWriter.WriteAsync(JsonSerializer.Serialize(settings, SerializerOptions), cancellationToken);
        auditStore.AddAuditEvent(new AuditEvent(
            Guid.NewGuid().ToString("n"),
            AuditEventType.SetupSaved,
            "Dashboard settings were updated. Restart the API to load the changes.",
            null,
            null,
            "dashboard",
            DateTimeOffset.UtcNow));

        return new DashboardSettings(
            request.DefaultAgentId.Trim(),
            request.IncludeRecommendedFeeds,
            request.Services.Select(service => new ServiceSetting(
                service.Id.Trim(),
                service.Name.Trim(),
                service.Kind,
                service.Description.Trim(),
                NormalizeOptionalUri(service.Url, $"URL for {service.Name}"),
                NormalizeOptionalUri(service.HealthUrl, $"health URL for {service.Name}"),
                !service.ClearApiKey && (!string.IsNullOrWhiteSpace(service.ApiKey) || existingServices.GetValueOrDefault(service.Id)?.ApiKey is not null),
                service.RestartEnabled)).ToArray(),
            feeds.Select(feed => new NewsFeedSetting(feed.Name, feed.Url, Enum.Parse<NewsContentKind>(feed.Kind), feed.Category, feed.ProviderUrl)).ToArray(),
            true);
    }

    private static void ValidateSettings(UpdateDashboardSettingsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DefaultAgentId))
        {
            throw new InvalidOperationException("Default agent ID is required.");
        }

        if (request.Services is null || request.NewsFeeds is null)
        {
            throw new InvalidOperationException("Services and custom feeds are required.");
        }

        if (request.Services.Any(service => string.IsNullOrWhiteSpace(service.Id) || string.IsNullOrWhiteSpace(service.Name)))
        {
            throw new InvalidOperationException("Every service needs an ID and name.");
        }

        if (request.Services.Select(service => service.Id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Services.Count)
        {
            throw new InvalidOperationException("Service IDs must be unique.");
        }

        if (request.NewsFeeds.Any(feed => string.IsNullOrWhiteSpace(feed.Name) || string.IsNullOrWhiteSpace(feed.Url)))
        {
            throw new InvalidOperationException("Every custom feed needs a name and URL.");
        }
    }

    private static object ToService(ServiceSetupRequest service)
        => new
        {
            service.Id,
            service.Name,
            Kind = service.Kind.ToString(),
            service.Description,
            Url = NormalizeUri(service.Url),
            HealthUrl = NormalizeUri(service.HealthUrl),
            service.ApiKey,
            service.RestartEnabled
        };

    private static object ToFeed(NewsFeedSetupRequest feed)
        => new
        {
            feed.Name,
            Url = NormalizeUri(feed.Url)
        };

    private static string? NormalizeUri(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.ToString() : null;

    private static string? NormalizeOptionalUri(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return NormalizeRequiredUri(value, fieldName);
    }

    private static string NormalizeRequiredUri(string value, string fieldName)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{fieldName} must be a valid HTTP or HTTPS URL.");
        }

        return uri.ToString();
    }

    private static string UseOrGenerate(string? value)
        => string.IsNullOrWhiteSpace(value) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : value;

    private static bool IsPlaceholder(string? value)
        => string.IsNullOrWhiteSpace(value) || value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase);
}

public interface ILocalSettingsWriter
{
    Task WriteAsync(string json, CancellationToken cancellationToken);
}

public sealed class LocalSettingsWriter : ILocalSettingsWriter
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task WriteAsync(string json, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        var temporaryPath = $"{path}.{Guid.NewGuid():n}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            gate.Release();
        }
    }
}
