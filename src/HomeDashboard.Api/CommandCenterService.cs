using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Api;

public interface ICommandCenterService
{
    Task<CommandCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    CommandCenterSnapshot Upsert(CommandCenterItemRequest request);
    bool Delete(string kind, string id);
    CommandCenterSnapshot ApplyBatch(CommandCenterBatchRequest request);
    Task<CommandCenterActionResult> ExecuteAsync(CommandCenterActionRequest request, CancellationToken cancellationToken);
    IReadOnlyList<CommandCenterSearchResult> Search(string query);
    Task<AssistantResponse> AskAsync(AssistantRequest request, CancellationToken cancellationToken);
    IntegrationStatus UpdateIntegration(string id, UpdateIntegrationRequest request);
    CommandCenterNotification Ingest(CommandCenterWebhook webhook);
    IReadOnlyList<FileWorkspaceEntry> BrowseFiles(string? path);
    Task<IReadOnlyList<SystemLogEntry>> GetSystemLogsAsync(int count, CancellationToken cancellationToken);
    HouseholdProfile? AuthenticateProfile(string? username, string? password);
    CommandCenterArchive Export();
    void Restore(CommandCenterArchive archive);
    DiscordBotConfiguration? GetDiscordConfiguration();
    void SetIntegrationConnection(string id, bool connected, string status);
}

public sealed record DiscordBotConfiguration(
    string Token,
    string Prefix,
    IReadOnlySet<ulong> AllowedUserIds,
    IReadOnlySet<ulong> AllowedChannelIds,
    IReadOnlySet<ulong> AllowedGuildIds,
    IReadOnlyDictionary<ulong, string> ProfileMappings);

public sealed class CommandCenterService : ICommandCenterService
{
    private static readonly IReadOnlyDictionary<string, (string Name, string[] Capabilities)> IntegrationCatalog =
        new Dictionary<string, (string, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["home-assistant"] = ("Home Assistant", ["entities", "scenes", "climate", "security", "energy"]),
            ["ntfy"] = ("ntfy", ["notifications", "action-buttons"]),
            ["ollama"] = ("Local assistant", ["chat", "briefings", "knowledge"]),
            ["microsoft-graph"] = ("Microsoft 365", ["calendar", "tasks", "mail"]),
            ["google"] = ("Google", ["calendar", "tasks", "mail"]),
            ["caldav"] = ("CalDAV", ["calendar", "tasks"]),
            ["github"] = ("GitHub", ["repositories", "releases", "ci", "security"]),
            ["unifi"] = ("UniFi", ["network", "clients", "wan"]),
            ["dns"] = ("Pi-hole / AdGuard Home", ["dns", "blocking", "clients"]),
            ["media-requests"] = ("Overseerr / Jellyseerr", ["search", "requests", "availability"]),
            ["backups"] = ("Backup systems", ["jobs", "retention", "restore-tests"]),
            ["ups"] = ("UPS / power", ["battery", "runtime", "shutdown"]),
            ["cameras"] = ("Cameras / doorbells", ["snapshots", "events"]),
            ["mqtt"] = ("MQTT bridge", ["publish", "events", "sensors"]),
            ["packages"] = ("Package tracking", ["deliveries"]),
            ["utilities"] = ("Utilities", ["electricity", "water", "gas"]),
            ["games"] = ("Game servers", ["players", "updates", "sessions"]),
            ["email"] = ("Email summary", ["inbox-summary"]),
            ["discord"] = ("Discord", ["remote-capture", "shopping", "agenda", "tasks"]),
            ["webhook"] = ("Generic webhooks", ["events", "actions", "custom-assets"]),
            ["windows"] = ("Windows workspace", ["files", "logs", "machine-actions"])
        };

    private readonly object gate = new();
    private readonly IHttpClientFactory clients;
    private readonly IAgentCommandStore commandStore;
    private readonly ILogger<CommandCenterService> logger;
    private readonly string defaultAgentId;
    private readonly string statePath;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private CommandCenterState state;

    public CommandCenterService(IOptions<DashboardOptions> options, IHttpClientFactory clients, IAgentCommandStore commandStore, ILogger<CommandCenterService> logger)
    {
        this.clients = clients;
        this.commandStore = commandStore;
        this.logger = logger;
        defaultAgentId = options.Value.DefaultAgentId;
        var dashboardStatePath = Path.GetFullPath(options.Value.DataPath, AppContext.BaseDirectory);
        statePath = Path.Combine(Path.GetDirectoryName(dashboardStatePath)!, "homedashboard-command-center.json");
        state = Load();
        EnsureCatalog();
    }

    public async Task<CommandCenterSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await RefreshHomeAssistantAsync(cancellationToken);
        await RefreshConnectorIntegrationsAsync(cancellationToken);
        lock (gate)
        {
            EvaluateAutomationsLocked();
            AddDerivedNotificationsLocked();
            return BuildSnapshotLocked();
        }
    }

    public CommandCenterSnapshot Upsert(CommandCenterItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 240)
        {
            throw new InvalidOperationException("A title between 1 and 240 characters is required.");
        }

        var kind = request.Kind.Trim().ToLowerInvariant();
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("n") : request.Id.Trim();
        var fields = request.Fields ?? new Dictionary<string, string>();
        lock (gate)
        {
            switch (kind)
            {
                case "task":
                    var existingTask = state.Tasks.FirstOrDefault(item => item.Id == id);
                    Replace(state.Tasks, new PersonalTask(id, request.Title.Trim(), request.Details, request.Category ?? "Inbox",
                        ParseEnum(fields.GetValueOrDefault("priority"), ItemPriority.Normal), request.Date,
                        ParseBool(fields.GetValueOrDefault("completed")), existingTask?.CreatedAt ?? DateTimeOffset.UtcNow), item => item.Id);
                    break;
                case "calendar":
                    Replace(state.Calendar, new CalendarEntry(id, request.Title.Trim(), request.Date ?? DateTimeOffset.UtcNow,
                        ParseDate(fields.GetValueOrDefault("endsAt")), request.Category ?? "Personal", fields.GetValueOrDefault("location"),
                        fields.GetValueOrDefault("url"), ParseBool(fields.GetValueOrDefault("allDay"))), item => item.Id);
                    break;
                case "note":
                    Replace(state.Notes, new QuickNote(id, request.Title.Trim(), request.Details ?? "",
                        Split(fields.GetValueOrDefault("tags")), ParseBool(fields.GetValueOrDefault("pinned")), DateTimeOffset.UtcNow), item => item.Id);
                    break;
                case "shopping":
                    Replace(state.Shopping, new ShoppingItem(id, request.Title.Trim(), request.Category ?? "Shopping",
                        ParseInt(fields.GetValueOrDefault("quantity"), 1), ParseBool(fields.GetValueOrDefault("completed")), DateTimeOffset.UtcNow), item => item.Id);
                    break;
                case "package":
                    Replace(state.Packages, new TrackedPackage(id, fields.GetValueOrDefault("carrier") ?? "Carrier",
                        fields.GetValueOrDefault("trackingNumber") ?? "", request.Title.Trim(), fields.GetValueOrDefault("status") ?? "Tracking",
                        request.Date, DateTimeOffset.UtcNow), item => item.Id);
                    break;
                case "media":
                    Replace(state.MediaRequests, new MediaRequestItem(id, request.Title.Trim(), fields.GetValueOrDefault("mediaType") ?? "Media",
                        fields.GetValueOrDefault("status") ?? "Requested", fields.GetValueOrDefault("requestedBy") ?? "dashboard", DateTimeOffset.UtcNow,
                        ParseUri(fields.GetValueOrDefault("artworkUrl")), EmptyToNull(fields.GetValueOrDefault("imdbId")),
                        ParseNullableInt(fields.GetValueOrDefault("tmdbId")), ParseNullableInt(fields.GetValueOrDefault("tvdbId")),
                        EmptyToNull(fields.GetValueOrDefault("source"))), item => item.Id);
                    break;
                case "automation":
                    Replace(state.Automations, new AutomationRule(id, request.Title.Trim(), fields.GetValueOrDefault("trigger") ?? "manual",
                        fields.GetValueOrDefault("condition"), fields.GetValueOrDefault("actionTool") ?? "notification.create",
                        fields.GetValueOrDefault("actionTarget"), !fields.TryGetValue("enabled", out var enabled) || ParseBool(enabled), null, null), item => item.Id);
                    break;
                case "profile":
                    var username = fields.GetValueOrDefault("username")?.Trim();
                    var password = fields.GetValueOrDefault("password");
                    var role = NormalizeRole(fields.GetValueOrDefault("role"));
                    if (!string.IsNullOrWhiteSpace(username) && (password?.Length ?? 0) < 8)
                        throw new InvalidOperationException("Household account passwords must be at least 8 characters.");
                    var profile = new HouseholdProfile(id, request.Title.Trim(), role,
                        fields.GetValueOrDefault("color"), !fields.TryGetValue("active", out var active) || ParseBool(active));
                    Replace(state.Profiles, profile, item => item.Id);
                    if (!string.IsNullOrWhiteSpace(username) && password is not null)
                    {
                        state.Accounts.RemoveAll(item => item.Username.Equals(username, StringComparison.OrdinalIgnoreCase) || item.ProfileId == id);
                        state.Accounts.Add(new HouseholdAccount(id, username, ApiKeyValidator.HashSecret(password)));
                    }
                    break;
                case "asset":
                    Replace(state.Assets, new OperationalAsset(id, request.Category ?? "Custom", request.Title.Trim(),
                        fields.GetValueOrDefault("status") ?? "Unknown", request.Details, fields.Where(pair => pair.Key.StartsWith("metric.", StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(pair => pair.Key[7..], pair => pair.Value), DateTimeOffset.UtcNow, fields.GetValueOrDefault("url")), item => item.Id);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported command-center item kind '{request.Kind}'.");
            }

            SaveLocked();
            return BuildSnapshotLocked();
        }
    }

    public bool Delete(string kind, string id)
    {
        lock (gate)
        {
            var removed = kind.ToLowerInvariant() switch
            {
                "task" => state.Tasks.RemoveAll(item => item.Id == id) > 0,
                "calendar" => state.Calendar.RemoveAll(item => item.Id == id) > 0,
                "note" => state.Notes.RemoveAll(item => item.Id == id) > 0,
                "shopping" => state.Shopping.RemoveAll(item => item.Id == id) > 0,
                "package" => state.Packages.RemoveAll(item => item.Id == id) > 0,
                "media" => state.MediaRequests.RemoveAll(item => item.Id == id) > 0,
                "automation" => state.Automations.RemoveAll(item => item.Id == id) > 0,
                "profile" => RemoveProfileLocked(id),
                "asset" => state.Assets.RemoveAll(item => item.Id == id) > 0,
                _ => false
            };
            if (removed) SaveLocked();
            return removed;
        }
    }

    public CommandCenterSnapshot ApplyBatch(CommandCenterBatchRequest request)
    {
        var actions = request.Actions ?? [];
        var deletes = request.Deletes ?? [];
        if (actions.Count + deletes.Count is 0 or > 500)
            throw new InvalidOperationException("A batch must contain between 1 and 500 operations.");

        lock (gate)
        {
            foreach (var action in actions)
            {
                var tool = action.Tool.Trim().ToLowerInvariant();
                var targetExists = tool switch
                {
                    "task.toggle" => state.Tasks.Any(item => item.Id == action.Target),
                    "shopping.toggle" => state.Shopping.Any(item => item.Id == action.Target),
                    "notification.ack" => state.Inbox.Any(item => item.Id == action.Target),
                    _ => throw new InvalidOperationException($"Tool '{action.Tool}' is not available in a batch.")
                };
                if (!targetExists) throw new InvalidOperationException($"The target for '{action.Tool}' was not found.");
            }
            foreach (var deletion in deletes)
            {
                if (deletion.Kind.Trim().ToLowerInvariant() is not ("task" or "calendar" or "note" or "shopping" or "package" or "media"))
                    throw new InvalidOperationException($"Items of kind '{deletion.Kind}' cannot be batch deleted.");
            }

            foreach (var action in actions)
            {
                var tool = action.Tool.Trim().ToLowerInvariant();
                var result = ExecuteLocalLocked(tool, action);
                if (!result.Succeeded) throw new InvalidOperationException(result.Message);
                state.Activity.Add(new CommandCenterActivity(Guid.NewGuid().ToString("n"), tool, action.Target,
                    result.Message, DateTimeOffset.UtcNow, true));
            }

            foreach (var deletion in deletes)
            {
                var kind = deletion.Kind.Trim().ToLowerInvariant();
                var removed = kind switch
                {
                    "task" => state.Tasks.RemoveAll(item => item.Id == deletion.Id) > 0,
                    "calendar" => state.Calendar.RemoveAll(item => item.Id == deletion.Id) > 0,
                    "note" => state.Notes.RemoveAll(item => item.Id == deletion.Id) > 0,
                    "shopping" => state.Shopping.RemoveAll(item => item.Id == deletion.Id) > 0,
                    "package" => state.Packages.RemoveAll(item => item.Id == deletion.Id) > 0,
                    "media" => state.MediaRequests.RemoveAll(item => item.Id == deletion.Id) > 0,
                    _ => throw new InvalidOperationException($"Items of kind '{deletion.Kind}' cannot be batch deleted.")
                };
                if (removed)
                    state.Activity.Add(new CommandCenterActivity(Guid.NewGuid().ToString("n"), $"{kind}.delete", deletion.Id,
                        $"{kind} removed.", DateTimeOffset.UtcNow, true));
            }

            Trim(state.Activity, 500);
            SaveLocked();
            return BuildSnapshotLocked();
        }
    }

    public async Task<CommandCenterActionResult> ExecuteAsync(CommandCenterActionRequest request, CancellationToken cancellationToken)
    {
        var tool = request.Tool.Trim().ToLowerInvariant();
        if (RequiresConfirmation(tool) && !request.Confirmed)
        {
            return new CommandCenterActionResult(false, "Confirm this action before it runs.", true);
        }

        try
        {
            CommandCenterActionResult result;
            lock (gate)
            {
                result = ExecuteLocalLocked(tool, request);
                if (result.Succeeded)
                {
                    state.Activity.Add(new CommandCenterActivity(Guid.NewGuid().ToString("n"), tool, request.Target, result.Message, DateTimeOffset.UtcNow, true));
                    Trim(state.Activity, 500);
                    SaveLocked();
                }
            }

            if (result.Message != "external") return result;
            result = tool switch
            {
                "notification.send" => await SendNtfyAsync(request, cancellationToken),
                "homeassistant.call" => await CallHomeAssistantAsync(request, cancellationToken),
                "webhook.send" or "mqtt.publish" => await SendWebhookAsync(tool, request, cancellationToken),
                "machine.wake" => await WakeMachineAsync(request, cancellationToken),
                "machine.lock" => QueueMachine(AgentCommandKind.LockComputer, request),
                "machine.sleep" => QueueMachine(AgentCommandKind.SleepComputer, request),
                "machine.restart" => QueueMachine(AgentCommandKind.RestartComputer, request),
                "machine.shutdown" => QueueMachine(AgentCommandKind.ShutdownComputer, request),
                _ => new CommandCenterActionResult(false, $"Tool '{request.Tool}' is not registered.")
            };
            RecordExternalActivity(tool, request.Target, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Command center action {Tool} failed", request.Tool);
            var result = new CommandCenterActionResult(false, ex.Message);
            RecordExternalActivity(tool, request.Target, result);
            return result;
        }
    }

    public IReadOnlyList<CommandCenterSearchResult> Search(string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return [];
        lock (gate)
        {
            var results = new List<CommandCenterSearchResult>();
            AddSearch(results, state.Tasks, "Task", item => item.Id, item => item.Title, item => item.Details, needle);
            AddSearch(results, state.Calendar, "Calendar", item => item.Id, item => item.Title, item => item.Location, needle);
            AddSearch(results, state.Notes, "Note", item => item.Id, item => item.Title, item => item.Body, needle);
            AddSearch(results, state.Shopping, "Shopping", item => item.Id, item => item.Name, item => item.List, needle);
            AddSearch(results, state.Packages, "Package", item => item.Id, item => item.Description, item => $"{item.Carrier} {item.Status}", needle);
            AddSearch(results, state.MediaRequests, "Media", item => item.Id, item => item.Title, item => item.Status, needle);
            AddSearch(results, state.Assets, "System", item => item.Id, item => item.Name, item => $"{item.Category} {item.Detail}", needle);
            AddSearch(results, state.HomeEntities, "Home", item => item.Id, item => item.Name, item => $"{item.Domain} {item.State}", needle);
            AddSearch(results, state.Integrations, "Integration", item => item.Id, item => item.Name, item => item.Kind, needle);
            return results.OrderByDescending(item => item.Score).ThenBy(item => item.Title).Take(60).ToArray();
        }
    }

    public async Task<AssistantResponse> AskAsync(AssistantRequest request, CancellationToken cancellationToken)
    {
        var message = request.Message.Trim();
        if (message.Length == 0) throw new InvalidOperationException("Enter a question or command.");
        CommandCenterSnapshot snapshot;
        lock (gate) snapshot = BuildSnapshotLocked();

        var lower = message.ToLowerInvariant();
        var proposed = new List<CommandCenterActionRequest>();
        if (lower.Contains("complete") && snapshot.Tasks.FirstOrDefault(item => lower.Contains(item.Title.ToLowerInvariant())) is { } task)
            proposed.Add(new CommandCenterActionRequest("task.toggle", task.Id, false, new Dictionary<string, string> { ["completed"] = "true" }));
        if ((lower.Contains("wake") || lower.Contains("turn on")) && ExtractMac(message) is { } mac)
            proposed.Add(new CommandCenterActionRequest("machine.wake", mac));
        if (lower.Contains("away mode")) proposed.Add(new CommandCenterActionRequest("mode.set", "Away", true));
        if (lower.Contains("home mode")) proposed.Add(new CommandCenterActionRequest("mode.set", "Home", true));
        if (lower.Contains("sleep mode")) proposed.Add(new CommandCenterActionRequest("mode.set", "Sleep", true));

        var answer = BuildAssistantAnswer(lower, snapshot);
        var ollamaAnswer = await TryAskOllamaAsync(message, snapshot, cancellationToken);
        if (!string.IsNullOrWhiteSpace(ollamaAnswer)) answer = ollamaAnswer;

        return new AssistantResponse(answer,
        [
            new("Morning briefing", "Give me my morning briefing"),
            new("Needs attention", "What needs my attention?"),
            new("Today", "What is on my calendar today?"),
            new("Infrastructure", "Summarize systems and integrations")
        ], proposed, DateTimeOffset.UtcNow);
    }

    public IntegrationStatus UpdateIntegration(string id, UpdateIntegrationRequest request)
    {
        if (!IntegrationCatalog.TryGetValue(id, out var catalog)) throw new InvalidOperationException("Unknown integration.");
        Uri? parsedUrl = null;
        if (!id.Equals("discord", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(request.BaseUrl)
            && (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out parsedUrl) || parsedUrl.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("Integration URL must be an absolute HTTP or HTTPS URL.");

        lock (gate)
        {
            var existing = state.Integrations.First(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var secret = request.ClearSecret ? null : string.IsNullOrWhiteSpace(request.Secret) ? existing.Secret : request.Secret.Trim();
            var updated = existing with
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? catalog.Name : request.Name.Trim(),
                BaseUrl = parsedUrl?.AbsoluteUri,
                Enabled = request.Enabled,
                Secret = secret,
                Settings = request.Settings is null ? existing.Settings : existing.Settings
                    .Concat(request.Settings).GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase),
                Status = request.Enabled ? "Configured; connection will be checked on refresh." : "Disabled",
                LastCheckedAt = null
            };
            Replace(state.Integrations, updated, item => item.Id);
            SaveLocked();
            return ToStatus(updated);
        }
    }

    public CommandCenterNotification Ingest(CommandCenterWebhook webhook)
    {
        var notification = new CommandCenterNotification(Guid.NewGuid().ToString("n"), webhook.Severity,
            Limit(webhook.Source, 80), Limit(webhook.Title ?? webhook.Event, 160), Limit(webhook.Message ?? webhook.Event, 600), DateTimeOffset.UtcNow);
        lock (gate)
        {
            state.Inbox.Add(notification);
            if (webhook.Data is { } data && data.TryGetValue("assetCategory", out var category))
            {
                var id = data.GetValueOrDefault("assetId") ?? $"{webhook.Source}-{webhook.Event}".ToLowerInvariant().Replace(' ', '-');
                var metrics = data.Where(pair => pair.Key.StartsWith("metric.", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(pair => pair.Key[7..], pair => pair.Value);
                Replace(state.Assets, new OperationalAsset(id, category, webhook.Title ?? webhook.Source,
                    data.GetValueOrDefault("status") ?? "Online", webhook.Message, metrics, DateTimeOffset.UtcNow, data.GetValueOrDefault("url")), item => item.Id);
            }
            Trim(state.Inbox, 500);
            SaveLocked();
        }
        return notification;
    }

    public IReadOnlyList<FileWorkspaceEntry> BrowseFiles(string? path)
    {
        var integration = GetEnabledIntegration("windows");
        var roots = SplitRoots(integration?.Settings.GetValueOrDefault("roots"));
        if (roots.Length == 0) return [];
        var requested = string.IsNullOrWhiteSpace(path) ? roots[0] : Path.GetFullPath(path);
        if (!roots.Any(root => IsWithinRoot(requested, root))) throw new InvalidOperationException("That path is outside the configured file roots.");
        if (!Directory.Exists(requested)) throw new InvalidOperationException("Directory not found.");
        return new DirectoryInfo(requested).EnumerateFileSystemInfos().OrderByDescending(item => item is DirectoryInfo).ThenBy(item => item.Name)
            .Take(250).Select(item => new FileWorkspaceEntry(item.Name, item.FullName, item is DirectoryInfo,
                item is FileInfo file ? file.Length : 0, item.LastWriteTimeUtc)).ToArray();
    }

    public async Task<IReadOnlyList<SystemLogEntry>> GetSystemLogsAsync(int count, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || GetEnabledIntegration("windows") is not { Enabled: true }) return [];
        var maximum = Math.Clamp(count, 1, 200);
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("wevtutil.exe", $"qe System /c:{maximum} /rd:true /f:text")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) return [];
        return output.Split("Event[", StringSplitOptions.RemoveEmptyEntries).Take(maximum).Select(block =>
        {
            var lines = block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var date = lines.FirstOrDefault(line => line.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
            var source = lines.FirstOrDefault(line => line.StartsWith("Provider Name:", StringComparison.OrdinalIgnoreCase))?[14..].Trim() ?? "Windows";
            var level = lines.FirstOrDefault(line => line.StartsWith("Level:", StringComparison.OrdinalIgnoreCase))?[6..].Trim() ?? "Information";
            var message = lines.LastOrDefault() ?? "System event";
            return new SystemLogEntry(DateTimeOffset.TryParse(date, out var occurred) ? occurred : DateTimeOffset.UtcNow, level, source, Limit(message, 1000));
        }).ToArray();
    }

    public HouseholdProfile? AuthenticateProfile(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return null;
        lock (gate)
        {
            var account = state.Accounts.FirstOrDefault(item => item.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (account is null || !FixedEquals(account.PasswordHash, ApiKeyValidator.HashSecret(password))) return null;
            return state.Profiles.FirstOrDefault(item => item.Id == account.ProfileId && item.Active);
        }
    }

    public DiscordBotConfiguration? GetDiscordConfiguration()
    {
        lock (gate)
        {
            var integration = state.Integrations.FirstOrDefault(item => item.Id == "discord" && item.Enabled);
            if (integration is null || string.IsNullOrWhiteSpace(integration.Secret)) return null;
            return new DiscordBotConfiguration(integration.Secret,
                string.IsNullOrWhiteSpace(integration.Settings.GetValueOrDefault("prefix")) ? "!hd" : Limit(integration.Settings["prefix"].Trim(), 20),
                ParseIds(integration.Settings.GetValueOrDefault("allowedUserIds")),
                ParseIds(integration.Settings.GetValueOrDefault("allowedChannelIds")),
                ParseIds(integration.Settings.GetValueOrDefault("allowedGuildIds")),
                ParseProfileMappings(integration.Settings.GetValueOrDefault("profileMappings")));
        }
    }

    public void SetIntegrationConnection(string id, bool connected, string status)
    {
        lock (gate)
        {
            var existing = state.Integrations.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var limitedStatus = Limit(status, 300);
            if (existing is null || (existing.Connected == connected && existing.Status == limitedStatus)) return;
            MarkIntegrationLocked(id, connected, status);
            SaveLocked();
        }
    }

    public CommandCenterArchive Export()
    {
        lock (gate)
        {
            return new CommandCenterArchive(state.ActiveMode, state.Tasks.ToArray(), state.Calendar.ToArray(), state.Notes.ToArray(),
                state.Shopping.ToArray(), state.Packages.ToArray(), state.MediaRequests.ToArray(), state.Inbox.ToArray(),
                state.Integrations.Select(item => new IntegrationArchive(item.Id, item.Kind, item.Name, item.Enabled, item.BaseUrl,
                    new Dictionary<string, string>(item.Settings))).ToArray(), state.HomeEntities.ToArray(), state.Assets.ToArray(),
                state.Automations.ToArray(), state.Profiles.ToArray(), state.Activity.ToArray());
        }
    }

    public void Restore(CommandCenterArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        lock (gate)
        {
            state.ActiveMode = Limit(archive.ActiveMode, 40);
            ReplaceAll(state.Tasks, archive.Tasks, 5_000);
            ReplaceAll(state.Calendar, archive.Calendar, 5_000);
            ReplaceAll(state.Notes, archive.Notes, 5_000);
            ReplaceAll(state.Shopping, archive.Shopping, 5_000);
            ReplaceAll(state.Packages, archive.Packages, 2_000);
            ReplaceAll(state.MediaRequests, archive.MediaRequests, 2_000);
            ReplaceAll(state.Inbox, archive.Inbox, 5_000);
            ReplaceAll(state.HomeEntities, archive.HomeEntities, 10_000);
            ReplaceAll(state.Assets, archive.Assets, 5_000);
            ReplaceAll(state.Automations, archive.Automations, 2_000);
            ReplaceAll(state.Profiles, archive.Profiles, 100);
            ReplaceAll(state.Activity, archive.Activity, 5_000);

            foreach (var archived in archive.Integrations.Take(100))
            {
                var existing = state.Integrations.FirstOrDefault(item => item.Id.Equals(archived.Id, StringComparison.OrdinalIgnoreCase));
                var restored = new IntegrationConfig(archived.Id, archived.Kind, archived.Name, archived.Enabled, archived.BaseUrl,
                    existing?.Secret, new Dictionary<string, string>(archived.Settings), "Restored; awaiting connection check", null, false);
                Replace(state.Integrations, restored, item => item.Id);
            }

            var profileIds = state.Profiles.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            state.Accounts.RemoveAll(item => !profileIds.Contains(item.ProfileId));
            EnsureCatalogLocked();
            SaveLocked();
        }
    }

    private CommandCenterActionResult ExecuteLocalLocked(string tool, CommandCenterActionRequest request)
    {
        var target = request.Target ?? "";
        switch (tool)
        {
            case "task.toggle":
                return Toggle(state.Tasks, target, item => item with { Completed = ParseBool(request.Arguments?.GetValueOrDefault("completed"), !item.Completed) }, item => item.Id, "Task updated.");
            case "shopping.toggle":
                return Toggle(state.Shopping, target, item => item with { Completed = !item.Completed }, item => item.Id, "Shopping item updated.");
            case "notification.ack":
                return Toggle(state.Inbox, target, item => item with { Acknowledged = true }, item => item.Id, "Notification acknowledged.");
            case "notification.snooze":
                return Toggle(state.Inbox, target, item => item with { SnoozedUntil = DateTimeOffset.UtcNow.AddMinutes(ParseInt(request.Arguments?.GetValueOrDefault("minutes"), 60)) }, item => item.Id, "Notification snoozed.");
            case "mode.set":
                state.ActiveMode = string.IsNullOrWhiteSpace(target) ? "Home" : Limit(target, 40);
                return new CommandCenterActionResult(true, $"{state.ActiveMode} mode activated.");
            case "automation.run":
                var ruleIndex = state.Automations.FindIndex(item => item.Id == target);
                if (ruleIndex < 0) return new CommandCenterActionResult(false, "Automation not found.");
                var rule = state.Automations[ruleIndex];
                state.Automations[ruleIndex] = rule with { LastRunAt = DateTimeOffset.UtcNow, LastResult = "Queued by dashboard" };
                state.Inbox.Add(new CommandCenterNotification(Guid.NewGuid().ToString("n"), NotificationSeverity.Info, "Automation", rule.Name, $"Action {rule.ActionTool} was queued.", DateTimeOffset.UtcNow));
                return new CommandCenterActionResult(true, $"Automation '{rule.Name}' ran.");
            case "notification.create":
                state.Inbox.Add(new CommandCenterNotification(Guid.NewGuid().ToString("n"), NotificationSeverity.Info, "Dashboard", target.Length > 0 ? target : "Reminder", request.Arguments?.GetValueOrDefault("message") ?? "Reminder created.", DateTimeOffset.UtcNow));
                return new CommandCenterActionResult(true, "Notification created.");
            case "notification.send":
            case "homeassistant.call":
            case "webhook.send":
            case "mqtt.publish":
            case "machine.wake":
            case "machine.lock":
            case "machine.sleep":
            case "machine.restart":
            case "machine.shutdown":
                return new CommandCenterActionResult(false, "external");
            default:
                return new CommandCenterActionResult(false, $"Tool '{tool}' is not registered.");
        }
    }

    private async Task RefreshHomeAssistantAsync(CancellationToken cancellationToken)
    {
        IntegrationConfig? integration;
        lock (gate) integration = state.Integrations.FirstOrDefault(item => item.Id == "home-assistant" && item.Enabled);
        if (integration?.BaseUrl is null || string.IsNullOrWhiteSpace(integration.Secret)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(EnsureSlash(integration.BaseUrl)), "api/states"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.Secret);
            using var response = await clients.CreateClient("command-center").SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            var entities = document.RootElement.EnumerateArray().Take(300).Select(ToHomeEntity).ToList();
            lock (gate)
            {
                state.HomeEntities = entities;
                MarkIntegrationLocked(integration.Id, true, $"{entities.Count} entities available");
                SaveLocked();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (gate) MarkIntegrationLocked(integration.Id, false, ex.Message);
        }
    }

    private async Task RefreshConnectorIntegrationsAsync(CancellationToken cancellationToken)
    {
        IntegrationConfig[] integrations;
        lock (gate)
        {
            integrations = state.Integrations.Where(item => item.Enabled && item.BaseUrl is not null
                && item.Id is not ("home-assistant" or "ntfy" or "ollama" or "webhook" or "mqtt" or "discord")).ToArray();
        }

        await Parallel.ForEachAsync(integrations, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (integration, token) =>
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, integration.BaseUrl);
                if (!string.IsNullOrWhiteSpace(integration.Secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.Secret);
                using var response = await clients.CreateClient("command-center").SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<ConnectorPayload>(jsonOptions, token) ?? new();
                lock (gate)
                {
                    foreach (var item in payload.Assets.Take(500)) Replace(state.Assets, item with { UpdatedAt = DateTimeOffset.UtcNow }, asset => asset.Id);
                    foreach (var item in payload.Calendar.Take(500)) Replace(state.Calendar, item, entry => entry.Id);
                    foreach (var item in payload.Packages.Take(200)) Replace(state.Packages, item with { UpdatedAt = DateTimeOffset.UtcNow }, package => package.Id);
                    foreach (var item in payload.MediaRequests.Take(200)) Replace(state.MediaRequests, item, media => media.Id);
                    foreach (var item in payload.Notifications.Take(200)) AddOrReplaceNotificationLocked(item);
                    MarkIntegrationLocked(integration.Id, true, payload.Status ?? $"Connected; {payload.Assets.Count + payload.Calendar.Count + payload.Notifications.Count} items received");
                    SaveLocked();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (gate) MarkIntegrationLocked(integration.Id, false, ex.Message);
            }
        });
    }

    private async Task<CommandCenterActionResult> CallHomeAssistantAsync(CommandCenterActionRequest action, CancellationToken cancellationToken)
    {
        var integration = GetEnabledIntegration("home-assistant");
        if (integration?.BaseUrl is null || string.IsNullOrWhiteSpace(integration.Secret)) return new(false, "Home Assistant is not configured.");
        var domain = action.Arguments?.GetValueOrDefault("domain") ?? "homeassistant";
        var service = action.Arguments?.GetValueOrDefault("service") ?? "toggle";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(EnsureSlash(integration.BaseUrl)), $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.Secret);
        var payload = new Dictionary<string, object?> { ["entity_id"] = action.Target };
        if (action.Arguments?.GetValueOrDefault("temperature") is { } temperature && int.TryParse(temperature, out var value))
            payload["temperature"] = value;
        request.Content = JsonContent.Create(payload);
        using var response = await clients.CreateClient("command-center").SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, $"Home Assistant accepted {domain}.{service}.") : new(false, $"Home Assistant returned {(int)response.StatusCode}.");
    }

    private async Task<CommandCenterActionResult> SendNtfyAsync(CommandCenterActionRequest action, CancellationToken cancellationToken)
    {
        var integration = GetEnabledIntegration("ntfy");
        if (integration?.BaseUrl is null) return new(false, "ntfy is not configured.");
        var topic = integration.Settings.GetValueOrDefault("topic") ?? "homedashboard";
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(EnsureSlash(integration.BaseUrl)), topic));
        if (!string.IsNullOrWhiteSpace(integration.Secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.Secret);
        request.Content = new StringContent(action.Arguments?.GetValueOrDefault("message") ?? action.Target ?? "HomeDashboard notification", Encoding.UTF8, "text/plain");
        if (action.Arguments?.GetValueOrDefault("title") is { } title) request.Headers.TryAddWithoutValidation("Title", title);
        using var response = await clients.CreateClient("command-center").SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, "Notification sent.") : new(false, $"ntfy returned {(int)response.StatusCode}.");
    }

    private async Task<CommandCenterActionResult> SendWebhookAsync(string tool, CommandCenterActionRequest action, CancellationToken cancellationToken)
    {
        var integration = GetEnabledIntegration(tool == "mqtt.publish" ? "mqtt" : "webhook");
        if (integration?.BaseUrl is null) return new(false, $"{integration?.Name ?? "Connector"} is not configured.");
        using var request = new HttpRequestMessage(HttpMethod.Post, integration.BaseUrl);
        if (!string.IsNullOrWhiteSpace(integration.Secret)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", integration.Secret);
        request.Content = JsonContent.Create(new { target = action.Target, arguments = action.Arguments, source = "HomeDashboard", tool });
        using var response = await clients.CreateClient("command-center").SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode ? new(true, "Connector accepted the action.") : new(false, $"Connector returned {(int)response.StatusCode}.");
    }

    private static async Task<CommandCenterActionResult> WakeMachineAsync(CommandCenterActionRequest action, CancellationToken cancellationToken)
    {
        var bytes = (action.Target ?? "").Replace(":", "").Replace("-", "");
        if (bytes.Length != 12 || !bytes.All(Uri.IsHexDigit)) return new(false, "A valid MAC address is required.");
        var mac = Convert.FromHexString(bytes);
        var packet = Enumerable.Repeat((byte)0xff, 6).Concat(Enumerable.Range(0, 16).SelectMany(_ => mac)).ToArray();
        using var udp = new UdpClient { EnableBroadcast = true };
        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, 9), cancellationToken);
        return new(true, "Wake-on-LAN packet sent.");
    }

    private CommandCenterActionResult QueueMachine(AgentCommandKind kind, CommandCenterActionRequest action)
    {
        var agentId = string.IsNullOrWhiteSpace(action.Target) ? defaultAgentId : action.Target;
        var requestedBy = action.Arguments?.GetValueOrDefault("requestedBy") ?? "dashboard";
        var command = commandStore.EnqueueMachine(agentId, kind, new RestartRequest(requestedBy, $"Approved {kind} command", true));
        return new(true, $"{kind} queued for {agentId}.", AuditId: command.Id);
    }

    private async Task<string?> TryAskOllamaAsync(string message, CommandCenterSnapshot snapshot, CancellationToken cancellationToken)
    {
        var integration = GetEnabledIntegration("ollama");
        if (integration?.BaseUrl is null) return null;
        try
        {
            var context = $"Mode: {snapshot.ActiveMode}. Open tasks: {snapshot.Tasks.Count(item => !item.Completed)}. Upcoming events: {snapshot.Calendar.Count}. Unread alerts: {snapshot.Inbox.Count(item => !item.Acknowledged)}. Assets needing attention: {snapshot.Assets.Count(item => !IsHealthy(item.Status))}.";
            using var response = await clients.CreateClient("command-center").PostAsJsonAsync(new Uri(new Uri(EnsureSlash(integration.BaseUrl)), "api/chat"), new
            {
                model = integration.Settings.GetValueOrDefault("model") ?? "qwen3:4b",
                stream = false,
                messages = new[]
                {
                    new { role = "system", content = "You are the concise HomeDashboard assistant. Use only the supplied context. Never claim an action was executed." },
                    new { role = "system", content = context },
                    new { role = "user", content = message }
                }
            }, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            return document.RootElement.GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Optional Ollama assistant was unavailable");
            return null;
        }
    }

    private static string BuildAssistantAnswer(string message, CommandCenterSnapshot snapshot)
    {
        if (message.Contains("brief") || message.Contains("morning") || message.Contains("evening")) return snapshot.Briefing.Summary + " " + string.Join(" ", snapshot.Briefing.Highlights);
        if (message.Contains("attention") || message.Contains("wrong") || message.Contains("problem"))
        {
            var items = snapshot.Inbox.Where(item => !item.Acknowledged && (item.SnoozedUntil is null || item.SnoozedUntil <= DateTimeOffset.UtcNow)).Take(5).Select(item => item.Title).ToArray();
            return items.Length == 0 ? "Nothing currently needs attention." : $"The main items needing attention are: {string.Join("; ", items)}.";
        }
        if (message.Contains("calendar") || message.Contains("today"))
        {
            var events = snapshot.Calendar.Where(item => item.StartsAt.LocalDateTime.Date == DateTime.Today).Take(5).Select(item => $"{item.Title} at {item.StartsAt.LocalDateTime:t}").ToArray();
            return events.Length == 0 ? "There are no calendar entries today." : string.Join("; ", events) + ".";
        }
        if (message.Contains("task")) return $"You have {snapshot.Tasks.Count(item => !item.Completed)} open tasks, including {string.Join(", ", snapshot.Tasks.Where(item => !item.Completed).Take(3).Select(item => item.Title))}.";
        if (message.Contains("system") || message.Contains("infrastructure") || message.Contains("integration")) return $"{snapshot.Integrations.Count(item => item.Connected)} integrations are connected. {snapshot.Assets.Count(item => !IsHealthy(item.Status))} tracked assets need attention.";
        return $"I can summarize your day, search personal data, inspect infrastructure, and propose approved actions. Right now there are {snapshot.Tasks.Count(item => !item.Completed)} open tasks and {snapshot.Inbox.Count(item => !item.Acknowledged)} unread notifications.";
    }

    private CommandCenterSnapshot BuildSnapshotLocked()
    {
        var now = DateTimeOffset.UtcNow;
        var upcoming = state.Calendar.Where(item => item.StartsAt >= now.AddHours(-2)).OrderBy(item => item.StartsAt).Take(20).ToArray();
        var inbox = state.Inbox.Where(item => item.SnoozedUntil is null || item.SnoozedUntil <= now).OrderByDescending(item => item.CreatedAt).Take(200).ToArray();
        var attention = inbox.Count(item => !item.Acknowledged && item.Severity != NotificationSeverity.Info)
            + state.Tasks.Count(item => !item.Completed && item.DueAt < now)
            + state.Assets.Count(item => !IsHealthy(item.Status));
        var highlights = new List<string>();
        if (upcoming.FirstOrDefault() is { } next) highlights.Add($"Next: {next.Title} at {next.StartsAt.LocalDateTime:g}.");
        var due = state.Tasks.Count(item => !item.Completed && item.DueAt?.LocalDateTime.Date <= DateTime.Today);
        if (due > 0) highlights.Add($"{due} task{(due == 1 ? " is" : "s are")} due today or overdue.");
        var deliveries = state.Packages.Count(item => item.EstimatedDelivery?.LocalDateTime.Date == DateTime.Today);
        if (deliveries > 0) highlights.Add($"{deliveries} delivery expected today.");
        if (highlights.Count == 0) highlights.Add("Your schedule is clear and no urgent personal items are due.");
        var greeting = DateTime.Now.Hour < 12 ? "Good morning" : DateTime.Now.Hour < 18 ? "Good afternoon" : "Good evening";
        var summary = $"{greeting}. {state.Tasks.Count(item => !item.Completed)} tasks are open, {upcoming.Count(item => item.StartsAt.LocalDateTime.Date == DateTime.Today)} events are scheduled today, and {attention} items need attention.";
        var briefing = new DailyBriefing(greeting, summary, highlights, attention, now);

        return new CommandCenterSnapshot(now, state.ActiveMode, briefing,
            state.Tasks.OrderBy(item => item.Completed).ThenBy(item => item.DueAt ?? DateTimeOffset.MaxValue).ToArray(), upcoming,
            state.Notes.OrderByDescending(item => item.Pinned).ThenByDescending(item => item.UpdatedAt).ToArray(),
            state.Shopping.OrderBy(item => item.Completed).ThenBy(item => item.Name).ToArray(),
            state.Packages.OrderBy(item => item.EstimatedDelivery ?? DateTimeOffset.MaxValue).ToArray(),
            state.MediaRequests.OrderByDescending(item => item.RequestedAt).ToArray(), inbox,
            state.Integrations.Select(ToStatus).OrderBy(item => item.Name).ToArray(), state.HomeEntities.ToArray(),
            state.Assets.OrderBy(item => item.Category).ThenBy(item => item.Name).ToArray(),
            state.Automations.OrderBy(item => item.Name).ToArray(), state.Profiles.OrderBy(item => item.DisplayName).ToArray(),
            state.Activity.OrderByDescending(item => item.OccurredAt).Take(200).ToArray());
    }

    private void AddDerivedNotificationsLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var task in state.Tasks.Where(item => !item.Completed && item.DueAt < now))
            AddOrReplaceNotificationLocked(new CommandCenterNotification($"task-overdue-{task.Id}", task.Priority is ItemPriority.Urgent or ItemPriority.High ? NotificationSeverity.Critical : NotificationSeverity.Warning, "Tasks", $"Overdue: {task.Title}", task.Details ?? "This task is past its due date.", now, Actions: [new("Complete", "task.toggle", task.Id, false)]));
        foreach (var package in state.Packages.Where(item => item.EstimatedDelivery?.LocalDateTime.Date == DateTime.Today))
            AddOrReplaceNotificationLocked(new CommandCenterNotification($"delivery-{package.Id}", NotificationSeverity.Info, "Deliveries", $"Arriving today: {package.Description}", $"{package.Carrier}: {package.Status}", now));
        Trim(state.Inbox, 500);
    }

    private void EvaluateAutomationsLocked()
    {
        var now = DateTimeOffset.Now;
        for (var index = 0; index < state.Automations.Count; index++)
        {
            var rule = state.Automations[index];
            if (!rule.Enabled || !AutomationIsDue(rule, now)) continue;
            var requiresApproval = RequiresConfirmation(rule.ActionTool) || rule.ActionTool is not "notification.create";
            var action = new NotificationAction(requiresApproval ? "Review action" : "Open", rule.ActionTool, rule.ActionTarget, requiresApproval);
            AddOrReplaceNotificationLocked(new CommandCenterNotification($"automation-{rule.Id}-{now:yyyyMMddHHmm}", NotificationSeverity.Info,
                "Automation", rule.Name, requiresApproval ? $"Rule is ready to run {rule.ActionTool}." : rule.ActionTarget ?? "Scheduled reminder", now,
                Actions: [action]));
            state.Automations[index] = rule with { LastRunAt = now, LastResult = requiresApproval ? "Awaiting approval" : "Notification created" };
        }
    }

    private bool AutomationIsDue(AutomationRule rule, DateTimeOffset now)
    {
        var trigger = rule.Trigger.Trim().ToLowerInvariant();
        if (trigger.StartsWith("daily at ") && TimeOnly.TryParse(trigger[9..], out var time))
            return now.TimeOfDay >= time.ToTimeSpan() && rule.LastRunAt?.LocalDateTime.Date != now.Date;
        if (trigger.StartsWith("every ") && int.TryParse(new string(trigger[6..].TakeWhile(char.IsDigit).ToArray()), out var minutes))
            return rule.LastRunAt is null || now - rule.LastRunAt >= TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 10080));
        if (trigger.Equals("task overdue", StringComparison.OrdinalIgnoreCase))
            return state.Tasks.Any(item => !item.Completed && item.DueAt < now) && (rule.LastRunAt is null || now - rule.LastRunAt >= TimeSpan.FromHours(1));
        if (trigger.StartsWith("mode "))
            return state.ActiveMode.Equals(trigger[5..], StringComparison.OrdinalIgnoreCase) && (rule.LastRunAt is null || now - rule.LastRunAt >= TimeSpan.FromHours(1));
        return false;
    }

    private void AddOrReplaceNotificationLocked(CommandCenterNotification notification)
    {
        if (state.Inbox.Any(item => item.Id == notification.Id)) return;
        state.Inbox.Add(notification);
        SaveLocked();
    }

    private void EnsureCatalog()
    {
        lock (gate)
        {
            EnsureCatalogLocked();
            SaveLocked();
        }
    }

    private void EnsureCatalogLocked()
    {
        foreach (var (id, item) in IntegrationCatalog)
            if (state.Integrations.All(existing => !existing.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                state.Integrations.Add(new IntegrationConfig(id, id, item.Name, false, null, null, new Dictionary<string, string>(), "Not configured", null, false));
        if (state.Profiles.Count == 0) state.Profiles.Add(new HouseholdProfile("owner", "Owner", "Administrator", "#5eead4", true));
    }

    private CommandCenterState Load()
    {
        if (!File.Exists(statePath)) return new CommandCenterState();
        try { return JsonSerializer.Deserialize<CommandCenterState>(File.ReadAllText(statePath), jsonOptions) ?? new(); }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Command-center state could not be loaded; preserving the unreadable file and starting with empty state");
            return new();
        }
    }

    private void SaveLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temp = statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, jsonOptions));
        File.Move(temp, statePath, true);
    }

    private IntegrationConfig? GetEnabledIntegration(string id) { lock (gate) return state.Integrations.FirstOrDefault(item => item.Id == id && item.Enabled); }
    private void MarkIntegrationLocked(string id, bool connected, string status)
    {
        var index = state.Integrations.FindIndex(item => item.Id == id);
        if (index >= 0) state.Integrations[index] = state.Integrations[index] with { Connected = connected, Status = Limit(status, 300), LastCheckedAt = DateTimeOffset.UtcNow };
    }
    private static IntegrationStatus ToStatus(IntegrationConfig item) => new(item.Id, item.Kind, item.Name, item.Enabled, item.Connected, item.Status, item.LastCheckedAt,
        IntegrationCatalog.GetValueOrDefault(item.Id).Capabilities ?? [], item.BaseUrl, !string.IsNullOrWhiteSpace(item.Secret),
        new Dictionary<string, string>(item.Settings, StringComparer.OrdinalIgnoreCase));
    private void RecordExternalActivity(string tool, string? target, CommandCenterActionResult result)
    {
        lock (gate) { state.Activity.Add(new(Guid.NewGuid().ToString("n"), tool, target, result.Message, DateTimeOffset.UtcNow, result.Succeeded)); Trim(state.Activity, 500); SaveLocked(); }
    }
    private static bool RequiresConfirmation(string tool) => tool is "homeassistant.call" or "webhook.send" or "mqtt.publish" or "machine.wake" or "machine.lock" or "machine.sleep" or "machine.restart" or "machine.shutdown";
    private static bool IsHealthy(string status) => status.Equals("online", StringComparison.OrdinalIgnoreCase) || status.Equals("healthy", StringComparison.OrdinalIgnoreCase) || status.Equals("ok", StringComparison.OrdinalIgnoreCase) || status.Equals("connected", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeRole(string? role) => role?.Trim().ToLowerInvariant() switch
    {
        null or "" or "member" => "Member",
        "administrator" => "Administrator",
        "viewer" => "Viewer",
        _ => throw new InvalidOperationException("Household role must be Administrator, Member, or Viewer.")
    };
    private static string EnsureSlash(string value) => value.EndsWith('/') ? value : value + "/";
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    private static Uri? ParseUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var date) ? date : null;
    private static T ParseEnum<T>(string? value, T fallback) where T : struct => Enum.TryParse<T>(value, true, out var result) ? result : fallback;
    private static bool ParseBool(string? value, bool fallback = false) => bool.TryParse(value, out var result) ? result : fallback;
    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out var result) ? result : fallback;
    private static int? ParseNullableInt(string? value) => int.TryParse(value, out var result) ? result : null;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string[] Split(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string[] SplitRoots(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToArray();
    private static IReadOnlySet<ulong> ParseIds(string? value) => string.IsNullOrWhiteSpace(value)
        ? new HashSet<ulong>()
        : value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => ulong.TryParse(item, out var id) ? id : 0).Where(id => id > 0).ToHashSet();
    private static IReadOnlyDictionary<ulong, string> ParseProfileMappings(string? value)
    {
        var mappings = new Dictionary<ulong, string>();
        if (string.IsNullOrWhiteSpace(value)) return mappings;

        foreach (var item in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split([':', '='], 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && ulong.TryParse(parts[0], out var userId) && userId > 0 && parts[1].Length > 0)
                mappings[userId] = parts[1];
        }

        return mappings;
    }
    private static bool IsWithinRoot(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left); var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
    private static string? ExtractMac(string value) => System.Text.RegularExpressions.Regex.Match(value, "(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}").Value is { Length: > 0 } mac ? mac : null;
    private static void Trim<T>(List<T> items, int maximum) { if (items.Count > maximum) items.RemoveRange(0, items.Count - maximum); }
    private bool RemoveProfileLocked(string id)
    {
        var removed = state.Profiles.RemoveAll(item => item.Id == id) > 0;
        if (removed) state.Accounts.RemoveAll(item => item.ProfileId == id);
        return removed;
    }
    private static void ReplaceAll<T>(List<T> target, IReadOnlyList<T> source, int maximum)
    {
        if (source.Count > maximum) throw new InvalidOperationException($"Archive contains more than {maximum} supported items.");
        target.Clear();
        target.AddRange(source);
    }
    private static void Replace<T>(List<T> items, T item, Func<T, string> id)
    {
        var index = items.FindIndex(existing => id(existing).Equals(id(item), StringComparison.OrdinalIgnoreCase));
        if (index >= 0) items[index] = item; else items.Add(item);
    }
    private static CommandCenterActionResult Toggle<T>(List<T> items, string id, Func<T, T> update, Func<T, string> key, string message)
    {
        var index = items.FindIndex(item => key(item) == id);
        if (index < 0) return new(false, "Item not found.");
        items[index] = update(items[index]);
        return new(true, message);
    }
    private static void AddSearch<T>(List<CommandCenterSearchResult> output, IEnumerable<T> items, string kind, Func<T, string> id, Func<T, string> title, Func<T, string?> subtitle, string needle)
    {
        foreach (var item in items)
        {
            var heading = title(item); var detail = subtitle(item);
            var titleIndex = heading.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            var detailIndex = detail?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (titleIndex < 0 && detailIndex < 0) continue;
            output.Add(new(id(item), kind, heading, detail, null, titleIndex == 0 ? 1 : titleIndex >= 0 ? .8 : .5));
        }
    }
    private static HomeEntity ToHomeEntity(JsonElement item)
    {
        var entityId = item.GetProperty("entity_id").GetString() ?? "entity";
        var state = item.GetProperty("state").GetString() ?? "unknown";
        var attributes = item.TryGetProperty("attributes", out var rawAttributes)
            ? rawAttributes.EnumerateObject().Where(property => property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False).Take(20).ToDictionary(property => property.Name, property => property.Value.ToString())
            : new Dictionary<string, string>();
        var domain = entityId.Split('.')[0];
        return new(entityId, attributes.GetValueOrDefault("friendly_name") ?? entityId, domain, state, attributes.GetValueOrDefault("area_name"), attributes,
            item.TryGetProperty("last_updated", out var updated) && DateTimeOffset.TryParse(updated.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow);
    }

    public sealed class CommandCenterState
    {
        public string ActiveMode { get; set; } = "Home";
        public List<PersonalTask> Tasks { get; init; } = [];
        public List<CalendarEntry> Calendar { get; init; } = [];
        public List<QuickNote> Notes { get; init; } = [];
        public List<ShoppingItem> Shopping { get; init; } = [];
        public List<TrackedPackage> Packages { get; init; } = [];
        public List<MediaRequestItem> MediaRequests { get; init; } = [];
        public List<CommandCenterNotification> Inbox { get; init; } = [];
        public List<IntegrationConfig> Integrations { get; init; } = [];
        public List<HomeEntity> HomeEntities { get; set; } = [];
        public List<OperationalAsset> Assets { get; init; } = [];
        public List<AutomationRule> Automations { get; init; } = [];
        public List<HouseholdProfile> Profiles { get; init; } = [];
        public List<HouseholdAccount> Accounts { get; init; } = [];
        public List<CommandCenterActivity> Activity { get; init; } = [];
    }

    public sealed record IntegrationConfig(string Id, string Kind, string Name, bool Enabled, string? BaseUrl, string? Secret,
        Dictionary<string, string> Settings, string Status, DateTimeOffset? LastCheckedAt, bool Connected);
    public sealed record HouseholdAccount(string ProfileId, string Username, string PasswordHash);
    public sealed class ConnectorPayload
    {
        public string? Status { get; init; }
        public List<OperationalAsset> Assets { get; init; } = [];
        public List<CalendarEntry> Calendar { get; init; } = [];
        public List<TrackedPackage> Packages { get; init; } = [];
        public List<MediaRequestItem> MediaRequests { get; init; } = [];
        public List<CommandCenterNotification> Notifications { get; init; } = [];
    }
}

