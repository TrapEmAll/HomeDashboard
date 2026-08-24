using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class SetupServiceTests
{
    [Fact]
    public void Setup_status_is_configured_when_password_hash_is_saved()
    {
        var setup = new SetupService(
            Options.Create(new DashboardOptions
            {
                DefaultAgentId = "server-pc",
                DataPath = "dashboard-state.test.json"
            }),
            Options.Create(new DashboardSecurityOptions
            {
                DashboardApiKey = "dashboard-secret",
                AgentApiKey = "agent-secret",
                DashboardPassword = "",
                DashboardPasswordHash = ApiKeyValidator.HashSecret("dashboard-password")
            }),
            new InMemoryAgentCommandStore(),
            new InMemorySettingsWriter());

        var status = setup.GetStatus();

        Assert.True(status.IsConfigured);
        Assert.False(status.UsesPlaceholderSecrets);
    }

    [Fact]
    public void Settings_mask_service_api_keys()
    {
        var setup = CreateSetup(out _);

        var settings = setup.GetSettings();

        var service = Assert.Single(settings.Services);
        Assert.True(service.HasApiKey);
        Assert.DoesNotContain("plex-secret", settings.ToString());
    }

    [Fact]
    public async Task Updating_settings_preserves_existing_secrets_and_service_api_key()
    {
        var setup = CreateSetup(out var writer);
        var request = new UpdateDashboardSettingsRequest(
            "media-pc",
            false,
            [new UpdateServiceSetting("plex", "Plex Media", ServiceKind.Plex, "Streaming", "http://media-pc:32400", "http://media-pc:32400/identity", "", false, true)],
            [new NewsFeedSetting("Test feed", "https://example.com/feed.xml", NewsContentKind.Article, "Testing", "https://example.com")]);

        var result = await setup.UpdateSettingsAsync(request, CancellationToken.None);

        Assert.True(result.RequiresRestart);
        Assert.True(Assert.Single(result.Services).HasApiKey);
        Assert.NotNull(writer.Json);
        Assert.Contains("dashboard-secret", writer.Json);
        Assert.Contains("agent-secret", writer.Json);
        Assert.Contains("password-hash", writer.Json);
        Assert.Contains("plex-secret", writer.Json);
        Assert.Contains("media-pc", writer.Json);
        Assert.Contains("1.1.1.1", writer.Json);
        Assert.Contains("hostDetailRefreshSeconds", writer.Json);
    }

    [Fact]
    public async Task Updating_settings_rejects_duplicate_service_ids()
    {
        var setup = CreateSetup(out _);
        var request = new UpdateDashboardSettingsRequest(
            "server-pc",
            true,
            [
                new UpdateServiceSetting("plex", "Plex", ServiceKind.Plex, "", null, null, null, false, false),
                new UpdateServiceSetting("PLEX", "Plex duplicate", ServiceKind.Plex, "", null, null, null, false, false)
            ],
            []);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.UpdateSettingsAsync(request, CancellationToken.None));

        Assert.Contains("unique", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_setup_synchronizes_generated_agent_credentials()
    {
        var writer = new InMemorySettingsWriter();
        var agentWriter = new InMemoryAgentSettingsWriter();
        var setup = new SetupService(
            Options.Create(new DashboardOptions()),
            Options.Create(new DashboardSecurityOptions()),
            new InMemoryAgentCommandStore(),
            writer,
            agentWriter);

        await setup.SaveAsync(new SetupRequest("password", null, null, "server-pc", [], []), CancellationToken.None);

        Assert.Equal("server-pc", agentWriter.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(agentWriter.ApiKey));
        Assert.Contains(agentWriter.ApiKey, writer.Json);
    }

    private static SetupService CreateSetup(out InMemorySettingsWriter writer)
    {
        writer = new InMemorySettingsWriter();
        return new SetupService(
            Options.Create(new DashboardOptions
            {
                DefaultAgentId = "server-pc",
                DataPath = "dashboard-state.test.json",
                IncludeRecommendedFeeds = true,
                NetworkProbeTarget = "1.1.1.1",
                NetworkProbeIntervalSeconds = 45,
                HostDetailRefreshSeconds = 90,
                Services = [new ServiceDefinition
                {
                    Id = "plex",
                    Name = "Plex",
                    Kind = ServiceKind.Plex,
                    Description = "Media server",
                    Url = new Uri("http://server-pc:32400"),
                    HealthUrl = new Uri("http://server-pc:32400/identity"),
                    ApiKey = "plex-secret"
                }],
                NewsFeeds = []
            }),
            Options.Create(new DashboardSecurityOptions
            {
                DashboardApiKey = "dashboard-secret",
                AgentApiKey = "agent-secret",
                DashboardPassword = "",
                DashboardPasswordHash = "password-hash"
            }),
            new InMemoryAgentCommandStore(),
            writer);
    }

    private sealed class InMemorySettingsWriter : ILocalSettingsWriter
    {
        public string? Json { get; private set; }

        public Task WriteAsync(string json, CancellationToken cancellationToken)
        {
            Json = json;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryAgentSettingsWriter : IAgentLocalSettingsWriter
    {
        public string? AgentId { get; private set; }
        public string? ApiKey { get; private set; }

        public Task WriteAsync(string agentId, string apiKey, CancellationToken cancellationToken)
        {
            AgentId = agentId;
            ApiKey = apiKey;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryAgentCommandStore : IAgentCommandStore
    {
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request)
            => throw new NotSupportedException();

        public AgentCommand? DequeueNext(string agentId)
            => throw new NotSupportedException();

        public void Complete(string agentId, string commandId, AgentCommandCompletion completion)
            => throw new NotSupportedException();

        public IReadOnlyList<AgentCommand> GetRecentCommands(int count)
            => [];

        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count)
            => [];

        public void AddAuditEvent(AuditEvent auditEvent)
        {
        }
    }
}
