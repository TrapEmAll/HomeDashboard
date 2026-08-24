using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class CommandCenterServiceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"homedashboard-command-center-{Guid.NewGuid():n}");

    [Fact]
    public async Task ItemsPersistSeparatelyAndAppearInSearchAndBriefing()
    {
        var service = CreateService();
        service.Upsert(new CommandCenterItemRequest("task", null, "Renew certificate", "Expires soon", "Infrastructure",
            DateTimeOffset.UtcNow.AddDays(-1), new Dictionary<string, string> { ["priority"] = "Urgent" }));

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Single(snapshot.Tasks);
        Assert.Contains(snapshot.Inbox, item => item.Id.StartsWith("task-overdue-", StringComparison.Ordinal));
        Assert.Contains(service.Search("certificate"), item => item.Kind == "Task");
        Assert.True(snapshot.Briefing.AttentionCount > 0);

        var reloaded = CreateService();
        Assert.Contains((await reloaded.GetSnapshotAsync(CancellationToken.None)).Tasks, item => item.Title == "Renew certificate");
    }

    [Fact]
    public void IntegrationResponsesMaskSavedSecrets()
    {
        var service = CreateService();
        var status = service.UpdateIntegration("home-assistant", new UpdateIntegrationRequest(
            "Home Assistant", "http://homeassistant.local:8123", false, "secret-token"));

        Assert.True(status.HasSecret);
        Assert.DoesNotContain("secret-token", System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task RiskyActionsRequireExplicitConfirmation()
    {
        var service = CreateService();

        var result = await service.ExecuteAsync(new CommandCenterActionRequest("machine.shutdown", null), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.RequiresConfirmation);
    }

    [Fact]
    public async Task LocalModeActionsAreAudited()
    {
        var service = CreateService();
        var result = await service.ExecuteAsync(new CommandCenterActionRequest("mode.set", "Movie", true), CancellationToken.None);

        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("Movie", snapshot.ActiveMode);
        Assert.Contains(snapshot.Activity, item => item.Tool == "mode.set" && item.Succeeded);
    }

    [Fact]
    public void HouseholdProfilesCanAuthenticateWithoutExposingPasswordHashes()
    {
        var service = CreateService();
        service.Upsert(new CommandCenterItemRequest("profile", null, "Alex", Fields: new Dictionary<string, string>
        {
            ["username"] = "alex",
            ["password"] = "a-strong-household-password",
            ["role"] = "Member"
        }));

        var profile = service.AuthenticateProfile("alex", "a-strong-household-password");

        Assert.NotNull(profile);
        Assert.Equal("Member", profile.Role);
        Assert.Null(service.AuthenticateProfile("alex", "wrong-password"));
    }

    [Fact]
    public void HouseholdProfilesRejectUnknownRoles()
    {
        var service = CreateService();

        var error = Assert.Throws<InvalidOperationException>(() => service.Upsert(new CommandCenterItemRequest(
            "profile", null, "Invalid", Fields: new Dictionary<string, string> { ["role"] = "Superuser" })));

        Assert.Contains("role", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchiveRestoresPersonalDataWithoutReplacingLocalConnectorSecrets()
    {
        var source = CreateService();
        source.Upsert(new CommandCenterItemRequest("task", null, "Back up the command center"));
        source.UpdateIntegration("home-assistant", new UpdateIntegrationRequest("Home Assistant", "http://source.local", false, "source-secret"));
        var archive = source.Export();

        var targetDirectory = Path.Combine(directory, "target");
        Directory.CreateDirectory(targetDirectory);
        var target = new CommandCenterService(Options.Create(new DashboardOptions { DataPath = Path.Combine(targetDirectory, "state.json") }),
            new StaticHttpClientFactory(), new NoopCommandStore(), NullLogger<CommandCenterService>.Instance);
        target.UpdateIntegration("home-assistant", new UpdateIntegrationRequest("Home Assistant", "http://target.local", false, "target-secret"));
        target.Restore(archive);

        var restored = await target.GetSnapshotAsync(CancellationToken.None);
        Assert.Contains(restored.Tasks, item => item.Title == "Back up the command center");
        Assert.True(restored.Integrations.Single(item => item.Id == "home-assistant").HasSecret);
        Assert.DoesNotContain("source-secret", System.Text.Json.JsonSerializer.Serialize(archive));
    }

    [Fact]
    public async Task DiscordCommandsAddAndCompleteRemoteListItems()
    {
        var service = CreateService();
        var processor = new DiscordCommandProcessor(service);

        var shopping = await processor.ProcessAsync("shopping add milk, bread | Groceries", "Alex", CancellationToken.None);
        var agenda = await processor.ProcessAsync("agenda add Dentist | 2030-09-03 14:00 | Downtown", "Alex", CancellationToken.None);
        var task = await processor.ProcessAsync("task add Renew certificate | 2030-09-01 18:00 | High | Home", "Alex", CancellationToken.None);
        var completed = await processor.ProcessAsync("shopping done milk", "Alex", CancellationToken.None);
        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Contains("2 items", shopping);
        Assert.Contains("Dentist", agenda);
        Assert.Contains("Renew certificate", task);
        Assert.Contains("Completed", completed);
        Assert.Equal(2, snapshot.Shopping.Count);
        Assert.Contains(snapshot.Shopping, item => item.Name == "milk" && item.Completed);
        Assert.Contains(snapshot.Calendar, item => item.Title == "Dentist" && item.Location == "Downtown");
        Assert.Contains(snapshot.Tasks, item => item.Title == "Renew certificate" && item.Priority == ItemPriority.High && item.List == "Home");
    }

    [Fact]
    public void DiscordConfigurationRequiresEnabledConnectorAndParsesAllowlists()
    {
        var service = CreateService();
        Assert.Null(service.GetDiscordConfiguration());
        service.UpdateIntegration("discord", new UpdateIntegrationRequest("Discord", null, true, "bot-token", new Dictionary<string, string>
        {
            ["prefix"] = "!home",
            ["allowedUserIds"] = "123, 456",
            ["allowedChannelIds"] = "789"
        }));

        var configuration = service.GetDiscordConfiguration();

        Assert.NotNull(configuration);
        Assert.Equal("!home", configuration.Prefix);
        Assert.Contains(123UL, configuration.AllowedUserIds);
        Assert.Contains(789UL, configuration.AllowedChannelIds);
    }

    private CommandCenterService CreateService()
    {
        Directory.CreateDirectory(directory);
        var options = Options.Create(new DashboardOptions { DataPath = Path.Combine(directory, "homedashboard-state.json") });
        return new CommandCenterService(options, new StaticHttpClientFactory(), new NoopCommandStore(), NullLogger<CommandCenterService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        GC.SuppressFinalize(this);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }

    private sealed class NoopCommandStore : IAgentCommandStore
    {
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request) => throw new NotSupportedException();
        public AgentCommand? DequeueNext(string agentId) => null;
        public void Complete(string agentId, string commandId, AgentCommandCompletion completion) { }
        public IReadOnlyList<AgentCommand> GetRecentCommands(int count) => [];
        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count) => [];
        public void AddAuditEvent(AuditEvent auditEvent) { }
    }
}
