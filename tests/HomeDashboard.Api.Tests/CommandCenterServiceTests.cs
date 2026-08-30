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
    public async Task BatchOperationsUpdateAndDeletePersonalItemsTogether()
    {
        var service = CreateService();
        var initial = service.Upsert(new CommandCenterItemRequest("task", null, "Finish release"));
        service.Upsert(new CommandCenterItemRequest("shopping", null, "Coffee"));
        service.Ingest(new CommandCenterWebhook("UPS", "battery", "Battery low", "Charge soon", NotificationSeverity.Warning));
        var task = initial.Tasks.Single();
        var shopping = initial.Shopping.SingleOrDefault() ?? (await service.GetSnapshotAsync(CancellationToken.None)).Shopping.Single();
        var alert = (await service.GetSnapshotAsync(CancellationToken.None)).Inbox.Single(item => item.Title == "Battery low");

        var result = service.ApplyBatch(new CommandCenterBatchRequest(
            [
                new CommandCenterActionRequest("task.toggle", task.Id, Arguments: new Dictionary<string, string> { ["completed"] = "true" }),
                new CommandCenterActionRequest("notification.ack", alert.Id)
            ],
            [new CommandCenterDeleteRequest("shopping", shopping.Id)]));

        Assert.True(result.Tasks.Single(item => item.Id == task.Id).Completed);
        Assert.True(result.Inbox.Single(item => item.Id == alert.Id).Acknowledged);
        Assert.Empty(result.Shopping);
    }

    [Fact]
    public async Task InvalidBatchDoesNotPartiallyMutateState()
    {
        var service = CreateService();
        var task = service.Upsert(new CommandCenterItemRequest("task", null, "Keep unchanged")).Tasks.Single();

        Assert.Throws<InvalidOperationException>(() => service.ApplyBatch(new CommandCenterBatchRequest(
        [
            new CommandCenterActionRequest("task.toggle", task.Id, Arguments: new Dictionary<string, string> { ["completed"] = "true" }),
            new CommandCenterActionRequest("machine.shutdown", "server")
        ])));

        Assert.False((await service.GetSnapshotAsync(CancellationToken.None)).Tasks.Single().Completed);
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
    public async Task DiscordStructuredCommandsListSearchSetModeAndRemoveItems()
    {
        var service = CreateService();
        var processor = new DiscordCommandProcessor(service);

        await processor.ProcessStructuredAsync("task", "add", new Dictionary<string, string>
        {
            ["title"] = "Review backups",
            ["priority"] = "Urgent",
            ["list"] = "Servers"
        }, "Alex", CancellationToken.None);

        var choices = processor.Autocomplete("task", "backup");
        var list = await processor.ProcessStructuredAsync("task", "list", new Dictionary<string, string>(), "Alex", CancellationToken.None);
        var search = await processor.ProcessAsync("search backups", "Alex", CancellationToken.None);
        var mode = await processor.ProcessStructuredAsync("system", "mode", new Dictionary<string, string> { ["mode"] = "Away" }, "Alex", CancellationToken.None);
        var reminder = await processor.ProcessStructuredAsync("reminder", "add", new Dictionary<string, string>
        {
            ["title"] = "Check the UPS",
            ["message"] = "Replace the battery this weekend"
        }, "Alex", CancellationToken.None);
        var removed = await processor.ProcessStructuredAsync("task", "remove", new Dictionary<string, string> { ["item"] = choices.Single().Id }, "Alex", CancellationToken.None);
        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Contains("Review backups", list);
        Assert.Contains("Review backups", search);
        Assert.Contains("Away mode", mode);
        Assert.Contains("created", reminder, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Removed", removed);
        Assert.Equal("Away", snapshot.ActiveMode);
        Assert.Empty(snapshot.Tasks);
    }

    [Fact]
    public async Task DiscordCommandsUpdateTasksShoppingNotesPackagesAndAttention()
    {
        var service = CreateService();
        var processor = new DiscordCommandProcessor(service);

        await processor.ProcessStructuredAsync("task", "add", new Dictionary<string, string>
        {
            ["title"] = "Pay server bill",
            ["due"] = "today 18:00",
            ["priority"] = "High",
            ["list"] = "Home"
        }, "Alex", CancellationToken.None);
        await processor.ProcessStructuredAsync("shopping", "add", new Dictionary<string, string>
        {
            ["items"] = "milk, bread",
            ["list"] = "Groceries"
        }, "Alex", CancellationToken.None);
        await processor.ProcessStructuredAsync("note", "add", new Dictionary<string, string>
        {
            ["title"] = "Router idea",
            ["body"] = "Replace switch in the rack"
        }, "Alex", CancellationToken.None);
        await processor.ProcessStructuredAsync("package", "add", new Dictionary<string, string>
        {
            ["description"] = "SSD",
            ["carrier"] = "UPS",
            ["tracking"] = "1Z123",
            ["eta"] = "tomorrow"
        }, "Alex", CancellationToken.None);

        var taskId = processor.Autocomplete("task", "server").Single().Id;
        var noteId = processor.Autocomplete("note", "router").Single().Id;
        var packageId = processor.Autocomplete("package", "ssd").Single().Id;
        var deferred = await processor.ProcessStructuredAsync("task", "defer", new Dictionary<string, string> { ["task"] = taskId, ["days"] = "2" }, "Alex", CancellationToken.None);
        var moved = await processor.ProcessStructuredAsync("task", "move", new Dictionary<string, string> { ["task"] = taskId, ["list"] = "Bills" }, "Alex", CancellationToken.None);
        var priority = await processor.ProcessStructuredAsync("task", "priority", new Dictionary<string, string> { ["task"] = taskId, ["priority"] = "Urgent" }, "Alex", CancellationToken.None);
        var bought = await processor.ProcessStructuredAsync("shopping", "buy_all", new Dictionary<string, string> { ["list"] = "Groceries" }, "Alex", CancellationToken.None);
        var pinned = await processor.ProcessStructuredAsync("note", "pin", new Dictionary<string, string> { ["note"] = noteId, ["pinned"] = "true" }, "Alex", CancellationToken.None);
        var package = await processor.ProcessStructuredAsync("package", "update", new Dictionary<string, string> { ["package"] = packageId, ["status"] = "Delivered" }, "Alex", CancellationToken.None);
        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Contains("Deferred", deferred);
        Assert.Contains("Moved", moved);
        Assert.Contains("Urgent", priority);
        Assert.Contains("2 shopping", bought);
        Assert.Contains("Pinned", pinned);
        Assert.Contains("Delivered", package);
        Assert.Contains(snapshot.Tasks, item => item.List == "Bills" && item.Priority == ItemPriority.Urgent);
        Assert.All(snapshot.Shopping, item => Assert.True(item.Completed));
        Assert.Contains(snapshot.Notes, item => item.Pinned);
        Assert.Contains(snapshot.Packages, item => item.Status == "Delivered");
    }

    [Fact]
    public async Task DiscordCommandsCreateAutomationBriefAskAndAcknowledgeAllAlerts()
    {
        var service = CreateService();
        var processor = new DiscordCommandProcessor(service);
        service.Ingest(new CommandCenterWebhook("UPS", "battery", "Battery low", "Runtime is under ten minutes", NotificationSeverity.Warning));
        service.Ingest(new CommandCenterWebhook("Plex", "transcode", "Plex warning", "Transcoder is busy", NotificationSeverity.Warning));

        var automation = await processor.ProcessStructuredAsync("automation", "add", new Dictionary<string, string>
        {
            ["name"] = "Morning check",
            ["trigger"] = "daily at 08:00",
            ["action_tool"] = "notification.create",
            ["action_target"] = "Check dashboard"
        }, "Alex", CancellationToken.None);
        var brief = await processor.ProcessStructuredAsync("assistant", "brief", new Dictionary<string, string>(), "Alex", CancellationToken.None);
        var answer = await processor.ProcessStructuredAsync("assistant", "ask", new Dictionary<string, string>
        {
            ["question"] = "What needs attention?"
        }, "Alex", CancellationToken.None);
        var ackAll = await processor.ProcessStructuredAsync("inbox", "ack_all", new Dictionary<string, string>(), "Alex", CancellationToken.None);
        var snapshot = await service.GetSnapshotAsync(CancellationToken.None);

        Assert.Contains("Automation created", automation);
        Assert.Contains(snapshot.Briefing.Greeting, brief);
        Assert.Contains("attention", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Acknowledged", ackAll);
        Assert.All(snapshot.Inbox, item => Assert.True(item.Acknowledged));
        Assert.Contains(snapshot.Automations, item => item.Name == "Morning check");
    }

    [Fact]
    public async Task DiscordCommandsManageInboxWithAutocomplete()
    {
        var service = CreateService();
        var processor = new DiscordCommandProcessor(service);
        service.Ingest(new CommandCenterWebhook("UPS", "battery", "Battery low", "Runtime is under ten minutes", NotificationSeverity.Warning));
        var alert = processor.Autocomplete("inbox", "battery").Single();

        var snoozed = await processor.ProcessStructuredAsync("inbox", "snooze", new Dictionary<string, string>
        {
            ["item"] = alert.Id,
            ["minutes"] = "30"
        }, "Alex", CancellationToken.None);

        Assert.Contains("30 minutes", snoozed);
        Assert.Empty(processor.Autocomplete("inbox", "battery"));
    }

    [Fact]
    public async Task DiscordMediaAutocompleteSubmitsAndRecordsCanonicalRelease()
    {
        var service = CreateService();
        var media = new FakeArrMediaRequestService();
        var processor = new DiscordCommandProcessor(service, media);

        var choices = await processor.AutocompleteAsync("media", "media_title", "Dune", CancellationToken.None);
        var response = await processor.ProcessStructuredAsync("media", "add", new Dictionary<string, string>
        {
            ["media_title"] = choices.Single().Id,
            ["search_now"] = "true"
        }, "Alex", CancellationToken.None);
        var saved = Assert.Single((await service.GetSnapshotAsync(CancellationToken.None)).MediaRequests);

        Assert.Contains("Dune: Part Two", choices.Single().Title);
        Assert.Contains("tt15239678", choices.Single().Subtitle);
        Assert.Contains("added", response);
        Assert.True(media.SearchNow);
        Assert.Equal("Dune: Part Two (2024)", saved.Title);
        Assert.Equal("tt15239678", saved.ImdbId);
        Assert.Equal(693134, saved.TmdbId);
        Assert.Equal("Radarr", saved.Source);
        Assert.Equal("Submitted", saved.Status);
    }

    [Fact]
    public async Task DiscordPrefixMediaRequestOffersChoicesForAmbiguousTitles()
    {
        var service = CreateService();
        var processor = new DiscordCommandProcessor(service, new AmbiguousArrMediaRequestService());

        var response = await processor.ProcessAsync("media add Dune", "Alex", CancellationToken.None);
        var choices = await processor.SearchMediaChoicesAsync("Dune", CancellationToken.None);
        var submitted = await processor.AddSelectedMediaAsync("movie:radarr:693134", "Alex", CancellationToken.None);
        var saved = Assert.Single((await service.GetSnapshotAsync(CancellationToken.None)).MediaRequests);

        Assert.StartsWith(DiscordCommandProcessor.AmbiguousMediaMessage, response);
        Assert.Equal(2, choices.Count);
        Assert.Contains(choices, choice => choice.Title == "Dune: Part Two (2024)" && choice.Subtitle!.Contains("tt15239678"));
        Assert.Contains("added", submitted);
        Assert.Equal("Dune: Part Two (2024)", saved.Title);
    }

    [Fact]
    public void DiscordSlashCommandCatalogBuildsGuidedCommandTree()
    {
        var command = DiscordSlashCommandCatalog.Build();

        Assert.Equal("home", command.Name.Value);
        Assert.Contains(command.Options.Value, option => option.Name == "task");
        Assert.Contains(command.Options.Value, option => option.Name == "shopping");
        Assert.Contains(command.Options.Value, option => option.Name == "system");
        Assert.Contains(command.Options.Value, option => option.Name == "device");
        Assert.Contains(command.Options.Value, option => option.Name == "automation");
        Assert.Contains(command.Options.Value, option => option.Name == "assistant");
        Assert.Contains(command.Options.Value, option => option.Name == "notify");
        Assert.Contains(command.Options.Value, option => option.Name == "help");
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
        Assert.Equal("!home", service.UpdateIntegration("discord", new UpdateIntegrationRequest(
            "Discord", null, true, null)).Settings["prefix"]);
    }

    [Fact]
    public async Task DiscordSettingsPersistAndLegacyEndpointIsIgnored()
    {
        var clientFactory = new RecordingHttpClientFactory();
        var service = CreateService(clientFactory);
        var status = service.UpdateIntegration("discord", new UpdateIntegrationRequest(
            "Discord", "null", true, "bot-token", new Dictionary<string, string>
            {
                ["prefix"] = "!home",
                ["allowedUserIds"] = "123",
                ["allowedChannelIds"] = "456",
                ["allowedGuildIds"] = "789"
            }));

        await service.GetSnapshotAsync(CancellationToken.None);
        var reloaded = CreateService().GetDiscordConfiguration();

        Assert.Null(status.BaseUrl);
        Assert.Equal(0, clientFactory.RequestCount);
        Assert.NotNull(reloaded);
        Assert.Equal("!home", reloaded.Prefix);
        Assert.Contains(123UL, reloaded.AllowedUserIds);
        Assert.Contains(456UL, reloaded.AllowedChannelIds);
        Assert.Contains(789UL, reloaded.AllowedGuildIds);
    }

    private CommandCenterService CreateService(IHttpClientFactory? clientFactory = null)
    {
        Directory.CreateDirectory(directory);
        var options = Options.Create(new DashboardOptions { DataPath = Path.Combine(directory, "homedashboard-state.json") });
        return new CommandCenterService(options, clientFactory ?? new StaticHttpClientFactory(), new NoopCommandStore(), NullLogger<CommandCenterService>.Instance);
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

    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        public int RequestCount { get; private set; }

        public HttpClient CreateClient(string name) => new(new RecordingHandler(() => RequestCount++));

        private sealed class RecordingHandler(Action onRequest) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                onRequest();
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("<html>not json</html>")
                });
            }
        }
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

    private sealed class FakeArrMediaRequestService : IArrMediaRequestService
    {
        private readonly ArrMediaLookupResult result = new("movie:radarr:693134", "Dune: Part Two", 2024, "Movie", "Radarr",
            "tt15239678", 693134, null, "Paul continues his journey.", "https://example.test/dune.jpg");

        public bool SearchNow { get; private set; }

        public Task<IReadOnlyList<ArrMediaLookupResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArrMediaLookupResult>>([result]);

        public Task<ArrMediaRequestResult> RequestAsync(string selectionId, bool searchNow, CancellationToken cancellationToken)
        {
            SearchNow = searchNow;
            return Task.FromResult(new ArrMediaRequestResult(true, "Dune: Part Two (2024) was added and a search was started in Radarr.", result, "Submitted"));
        }
    }

    private sealed class AmbiguousArrMediaRequestService : IArrMediaRequestService
    {
        private readonly ArrMediaLookupResult movie = new("movie:radarr:693134", "Dune: Part Two", 2024, "Movie", "Radarr",
            "tt15239678", 693134, null, null, null);
        private readonly ArrMediaLookupResult series = new("series:sonarr:366668", "Dune: Prophecy", 2024, "TV", "Sonarr",
            "tt10466872", null, 366668, null, null);

        public Task<IReadOnlyList<ArrMediaLookupResult>> SearchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArrMediaLookupResult>>([movie, series]);

        public Task<ArrMediaRequestResult> RequestAsync(string selectionId, bool searchNow, CancellationToken cancellationToken) =>
            Task.FromResult(new ArrMediaRequestResult(true, "Dune: Part Two (2024) was added and a search was started in Radarr.", movie, "Submitted"));
    }
}
