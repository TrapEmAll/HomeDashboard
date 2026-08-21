using System.Security.Cryptography;
using System.Text.Json;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public sealed class SetupService(
    IOptions<DashboardOptions> dashboardOptions,
    IOptions<DashboardSecurityOptions> securityOptions,
    IAgentCommandStore auditStore) : ISetupService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public SetupStatus GetStatus()
    {
        var security = securityOptions.Value;
        var dashboard = dashboardOptions.Value;
        var usesPlaceholders = IsPlaceholder(security.DashboardApiKey)
            || IsPlaceholder(security.AgentApiKey)
            || IsPlaceholder(security.DashboardPassword)
            || string.IsNullOrWhiteSpace(security.DashboardPasswordHash) && string.IsNullOrWhiteSpace(security.DashboardPassword);

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
                DashboardPasswordHash = ApiKeyValidator.HashSecret(request.DashboardPassword)
            },
            Dashboard = new
            {
                DefaultAgentId = string.IsNullOrWhiteSpace(request.DefaultAgentId) ? "server-pc" : request.DefaultAgentId,
                dashboardOptions.Value.DataPath,
                dashboardOptions.Value.AgentHistoryLimit,
                Services = request.Services.Select(ToService).ToArray(),
                NewsFeeds = request.NewsFeeds.Select(ToFeed).ToArray()
            }
        };

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings, SerializerOptions), cancellationToken);

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

    private static string UseOrGenerate(string? value)
        => string.IsNullOrWhiteSpace(value) ? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) : value;

    private static bool IsPlaceholder(string? value)
        => string.IsNullOrWhiteSpace(value) || value.StartsWith("change-me", StringComparison.OrdinalIgnoreCase);
}
