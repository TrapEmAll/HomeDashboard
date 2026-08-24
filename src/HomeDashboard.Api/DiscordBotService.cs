using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using HomeDashboard.Contracts;

namespace HomeDashboard.Api;

public sealed class DiscordBotService(
    ICommandCenterService commandCenter,
    DiscordCommandProcessor processor,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> lastCommands = new();
    private DiscordSocketClient? client;
    private DiscordBotConfiguration? activeConfiguration;
    private string? activeFingerprint;
    private string? registeredCommandFingerprint;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var configuration = commandCenter.GetDiscordConfiguration();
                    var fingerprint = configuration is null ? null : Fingerprint(configuration);
                    if (configuration is null)
                    {
                        await DisconnectAsync();
                    }
                    else if (configuration.AllowedUserIds.Count == 0)
                    {
                        await DisconnectAsync();
                        commandCenter.SetIntegrationConnection("discord", false, "Add at least one allowed Discord user ID.");
                    }
                    else if (client is null || fingerprint != activeFingerprint)
                    {
                        await DisconnectAsync();
                        await ConnectAsync(configuration, fingerprint!, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Discord bot connection failed");
                    commandCenter.SetIntegrationConnection("discord", false, $"Connection failed: {ex.Message}");
                    await DisconnectAsync();
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        await base.StopAsync(cancellationToken);
    }

    private async Task ConnectAsync(DiscordBotConfiguration configuration, string fingerprint, CancellationToken cancellationToken)
    {
        var socket = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
            AlwaysDownloadUsers = false,
            LogGatewayIntentWarnings = false
        });
        socket.MessageReceived += HandleMessageAsync;
        socket.SlashCommandExecuted += HandleSlashCommandAsync;
        socket.AutocompleteExecuted += HandleAutocompleteAsync;
        socket.Ready += HandleReadyAsync;
        socket.Disconnected += HandleDisconnectedAsync;
        socket.Log += HandleLogAsync;
        client = socket;
        activeConfiguration = configuration;
        activeFingerprint = fingerprint;
        await socket.LoginAsync(TokenType.Bot, configuration.Token);
        await socket.StartAsync();
        cancellationToken.ThrowIfCancellationRequested();
        commandCenter.SetIntegrationConnection("discord", false, "Connecting to Discord...");
    }

    private async Task DisconnectAsync()
    {
        var socket = client;
        client = null;
        activeConfiguration = null;
        activeFingerprint = null;
        if (socket is null) return;
        socket.MessageReceived -= HandleMessageAsync;
        socket.SlashCommandExecuted -= HandleSlashCommandAsync;
        socket.AutocompleteExecuted -= HandleAutocompleteAsync;
        socket.Ready -= HandleReadyAsync;
        socket.Disconnected -= HandleDisconnectedAsync;
        socket.Log -= HandleLogAsync;
        try { await socket.StopAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Discord socket stop failed"); }
        try { await socket.LogoutAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Discord logout failed"); }
        socket.Dispose();
    }

    private async Task HandleReadyAsync()
    {
        var socket = client;
        var configuration = activeConfiguration;
        if (socket is null || configuration is null) return;
        try
        {
            var registrationKey = $"{activeFingerprint}:{DiscordSlashCommandCatalog.SchemaVersion}";
            if (registeredCommandFingerprint != registrationKey)
            {
                var command = DiscordSlashCommandCatalog.Build();
                if (configuration.AllowedGuildIds.Count > 0)
                {
                    foreach (var guildId in configuration.AllowedGuildIds)
                    {
                        var guild = socket.GetGuild(guildId);
                        if (guild is null)
                        {
                            logger.LogWarning("Discord bot is not a member of configured guild {GuildId}", guildId);
                            continue;
                        }
                        await guild.BulkOverwriteApplicationCommandAsync([command]);
                    }
                }
                else
                {
                    await socket.BulkOverwriteGlobalApplicationCommandsAsync([command]);
                }
                registeredCommandFingerprint = registrationKey;
            }
            var scope = configuration.AllowedGuildIds.Count > 0 ? "server slash commands ready" : "global slash command registered";
            commandCenter.SetIntegrationConnection("discord", true, $"Connected as {socket.CurrentUser.Username}; {scope}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord slash command registration failed");
            commandCenter.SetIntegrationConnection("discord", true, $"Connected, but slash command registration failed: {ex.Message}");
        }
    }

    private Task HandleDisconnectedAsync(Exception exception)
    {
        commandCenter.SetIntegrationConnection("discord", false, exception.Message);
        return Task.CompletedTask;
    }

    private Task HandleLogAsync(LogMessage message)
    {
        if (message.Severity <= LogSeverity.Warning) logger.LogWarning(message.Exception, "Discord: {Message}", message.Message);
        else logger.LogDebug("Discord: {Message}", message.Message);
        return Task.CompletedTask;
    }

    private async Task HandleMessageAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message || message.Author.IsBot || message.Source != MessageSource.User) return;
        var configuration = activeConfiguration;
        if (configuration is null || !message.Content.StartsWith(configuration.Prefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!IsAuthorized(message, configuration)) return;
        var now = DateTimeOffset.UtcNow;
        if (lastCommands.TryGetValue(message.Author.Id, out var lastCommand) && now - lastCommand < TimeSpan.FromSeconds(2)) return;
        lastCommands[message.Author.Id] = now;

        var command = message.Content[configuration.Prefix.Length..].Trim();
        try
        {
            var response = await processor.ProcessAsync(command, message.Author.Username, CancellationToken.None);
            if (configuration.Prefix != "!hd") response = response.Replace("!hd", configuration.Prefix, StringComparison.Ordinal);
            await message.Channel.SendMessageAsync(response, allowedMentions: AllowedMentions.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord command failed for user {UserId}", message.Author.Id);
            await message.Channel.SendMessageAsync("The command could not be completed. Check HomeDashboard activity for details.", allowedMentions: AllowedMentions.None);
        }
    }

    private async Task HandleSlashCommandAsync(SocketSlashCommand interaction)
    {
        var configuration = activeConfiguration;
        if (configuration is null || interaction.Data.Name != DiscordSlashCommandCatalog.Name) return;
        if (!IsAuthorized(interaction, configuration))
        {
            await interaction.RespondAsync("This HomeDashboard command is not available to your Discord account or channel.", ephemeral: true);
            return;
        }

        try
        {
            await interaction.DeferAsync(ephemeral: true);
            var (area, action, options) = ReadSlashCommand(interaction.Data.Options);
            var response = await processor.ProcessStructuredAsync(area, action, options, interaction.User.Username, CancellationToken.None);
            await interaction.ModifyOriginalResponseAsync(message => message.Content = LimitMessage(response));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord slash command failed for user {UserId}", interaction.User.Id);
            if (interaction.HasResponded)
                await interaction.ModifyOriginalResponseAsync(message => message.Content = "The command could not be completed. Check HomeDashboard activity for details.");
            else
                await interaction.RespondAsync("The command could not be completed. Check HomeDashboard activity for details.", ephemeral: true);
        }
    }

    private async Task HandleAutocompleteAsync(SocketAutocompleteInteraction interaction)
    {
        var configuration = activeConfiguration;
        if (configuration is null || interaction.Data.CommandName != DiscordSlashCommandCatalog.Name || !IsAuthorized(interaction, configuration))
        {
            await interaction.RespondAsync([]);
            return;
        }

        try
        {
            var path = ReadOptionPath(interaction.Data.Options, interaction.Data.Current.Name);
            var query = interaction.Data.Current.Value?.ToString() ?? "";
            var results = processor.Autocomplete(path.Area, query)
                .Take(25).Select(item => new AutocompleteResult(LimitChoice(item.Title, item.Subtitle), item.Id));
            await interaction.RespondAsync(results);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Discord autocomplete failed for user {UserId}", interaction.User.Id);
            if (!interaction.HasResponded) await interaction.RespondAsync([]);
        }
    }

    private static (string Area, string Action, IReadOnlyDictionary<string, string> Options) ReadSlashCommand(
        IReadOnlyCollection<SocketSlashCommandDataOption> roots)
    {
        var root = roots.FirstOrDefault();
        if (root is null) return ("help", "show", new Dictionary<string, string>());
        if (root.Type == ApplicationCommandOptionType.SubCommand)
            return (root.Name, root.Name, Values(root.Options));
        var command = root.Options.FirstOrDefault();
        return (root.Name, command?.Name ?? "list", Values(command?.Options));
    }

    private static (string Area, string Action) ReadOptionPath(IEnumerable<AutocompleteOption> options, string currentName)
    {
        var values = options.ToArray();
        var area = values.FirstOrDefault(option => option.Type == ApplicationCommandOptionType.SubCommandGroup)?.Name
            ?? currentName switch
            {
                "shopping_item" => "shopping",
                "event" => "calendar",
                "alert" => "inbox",
                _ => currentName
            };
        return (area,
            values.FirstOrDefault(option => option.Type == ApplicationCommandOptionType.SubCommand)?.Name ?? "");
    }

    private static IReadOnlyDictionary<string, string> Values(IEnumerable<SocketSlashCommandDataOption>? options) =>
        options?.Where(option => option.Value is not null).ToDictionary(option => option.Name, option => option.Value!.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
        ?? new Dictionary<string, string>();

    private static string LimitChoice(string title, string? subtitle)
    {
        var value = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title} - {subtitle}";
        return value.Length <= 100 ? value : value[..97] + "...";
    }

    private static string LimitMessage(string value) => value.Length <= 1900 ? value : value[..1897] + "...";

    private static bool IsAuthorized(SocketUserMessage message, DiscordBotConfiguration configuration)
    {
        if (!configuration.AllowedUserIds.Contains(message.Author.Id)) return false;
        if (configuration.AllowedChannelIds.Count > 0 && !configuration.AllowedChannelIds.Contains(message.Channel.Id)) return false;
        if (message.Channel is SocketGuildChannel guildChannel && configuration.AllowedGuildIds.Count > 0
            && !configuration.AllowedGuildIds.Contains(guildChannel.Guild.Id)) return false;
        return message.Channel is SocketGuildChannel || configuration.AllowedGuildIds.Count == 0;
    }

    private static bool IsAuthorized(SocketInteraction interaction, DiscordBotConfiguration configuration)
    {
        if (!configuration.AllowedUserIds.Contains(interaction.User.Id)) return false;
        if (configuration.AllowedChannelIds.Count > 0 && !configuration.AllowedChannelIds.Contains(interaction.Channel.Id)) return false;
        if (configuration.AllowedGuildIds.Count > 0 && (interaction.GuildId is null || !configuration.AllowedGuildIds.Contains(interaction.GuildId.Value))) return false;
        return interaction.GuildId is not null || configuration.AllowedGuildIds.Count == 0;
    }

    private static string Fingerprint(DiscordBotConfiguration configuration)
    {
        var value = string.Join('|', configuration.Token, configuration.Prefix,
            string.Join(',', configuration.AllowedUserIds.Order()), string.Join(',', configuration.AllowedChannelIds.Order()),
            string.Join(',', configuration.AllowedGuildIds.Order()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed record DiscordCommandChoice(string Id, string Title, string? Subtitle = null);

public sealed class DiscordCommandProcessor(ICommandCenterService commandCenter)
{
    public async Task<string> ProcessAsync(string command, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Equals("help", StringComparison.OrdinalIgnoreCase)) return Help();
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var area = parts[0].ToLowerInvariant();
        if (area == "status") return await StatusAsync(cancellationToken);
        if (area == "search") return Search(command[parts[0].Length..].Trim());
        var action = parts.ElementAtOrDefault(1)?.ToLowerInvariant() ?? "list";
        var value = parts.ElementAtOrDefault(2)?.Trim() ?? "";
        if (action is "add" or "done" or "remove" or "ack" or "snooze" or "set" && value.Length == 0)
            return $"Missing command details. Use `/home help` or `!hd help`.";
        return await ProcessCoreAsync(area, action, value, actor, cancellationToken);
    }

    public Task<string> ProcessStructuredAsync(string area, string action, IReadOnlyDictionary<string, string> options,
        string actor, CancellationToken cancellationToken)
    {
        string Get(string name, string fallback = "") => options.GetValueOrDefault(name) ?? fallback;
        string Item() => area switch
        {
            "task" => Get("task", Get("item")),
            "shopping" => Get("shopping_item", Get("item")),
            "calendar" => Get("event", Get("item")),
            "note" => Get("note", Get("item")),
            "package" => Get("package", Get("item")),
            "media" => Get("media", Get("item")),
            "inbox" => Get("alert", Get("item")),
            "automation" => Get("automation", Get("item")),
            "device" => Get("device", Get("item")),
            _ => Get("item")
        };
        var value = (area, action) switch
        {
            ("task", "add") => string.Join(" | ", Get("title"), Get("due"), Get("priority", "Normal"), Get("list", "Inbox")),
            ("shopping", "add") => string.Join(" | ", Get("items"), Get("list", "Shopping")),
            ("calendar", "add") => string.Join(" | ", Get("title"), Get("when"), Get("location")),
            ("note", "add") => string.Join(" | ", Get("title"), Get("body")),
            ("package", "add") => string.Join(" | ", Get("description"), Get("carrier", "Carrier"), Get("tracking"), Get("eta")),
            ("media", "add") => string.Join(" | ", Get("title"), Get("type", "Media")),
            ("system", "search") => Get("query"),
            ("system", "mode") => Get("mode", "Home"),
            ("inbox", "snooze") => string.Join(" | ", Item(), Get("minutes", "60")),
            ("reminder", "add") => string.Join(" | ", Get("title"), Get("message")),
            ("device", "control") => string.Join(" | ", Item(), Get("service", "toggle"), Get("confirm", "false")),
            _ => Item()
        };
        return ProcessCoreAsync(area, action, value, actor, cancellationToken);
    }

    public IReadOnlyList<DiscordCommandChoice> Autocomplete(string area, string query)
    {
        var archive = commandCenter.Export();
        IEnumerable<DiscordCommandChoice> choices = area.ToLowerInvariant() switch
        {
            "task" => archive.Tasks.Select(item => new DiscordCommandChoice(item.Id, item.Title, item.Completed ? "Completed" : item.List)),
            "shopping" => archive.Shopping.Select(item => new DiscordCommandChoice(item.Id, item.Name, item.Completed ? "Purchased" : item.List)),
            "calendar" => archive.Calendar.Select(item => new DiscordCommandChoice(item.Id, item.Title, item.StartsAt.LocalDateTime.ToString("g"))),
            "note" => archive.Notes.Select(item => new DiscordCommandChoice(item.Id, item.Title, string.Join(", ", item.Tags))),
            "package" => archive.Packages.Select(item => new DiscordCommandChoice(item.Id, item.Description, $"{item.Carrier} - {item.Status}")),
            "media" => archive.MediaRequests.Select(item => new DiscordCommandChoice(item.Id, item.Title, $"{item.MediaType} - {item.Status}")),
            "inbox" => archive.Inbox.Where(item => !item.Acknowledged && (item.SnoozedUntil is null || item.SnoozedUntil <= DateTimeOffset.UtcNow))
                .Select(item => new DiscordCommandChoice(item.Id, item.Title, item.Source)),
            "automation" => archive.Automations.Select(item => new DiscordCommandChoice(item.Id, item.Name, item.Enabled ? item.Trigger : "Disabled")),
            "device" => archive.HomeEntities.Select(item => new DiscordCommandChoice(item.Id, item.Name, $"{item.State} - {item.Area}")),
            _ => []
        };
        var needle = query.Trim();
        if (needle.Length > 0)
            choices = choices.Where(item => item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (item.Subtitle?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        return choices.Take(25).ToArray();
    }

    private async Task<string> ProcessCoreAsync(string area, string action, string value, string actor, CancellationToken cancellationToken)
    {
        return (area, action) switch
        {
            ("shopping" or "shop", "add") => AddShopping(value),
            ("shopping" or "shop", "done") => await CompleteAsync("Shopping", "shopping.toggle", value, cancellationToken),
            ("shopping" or "shop", "list") => await ListAsync("shopping", cancellationToken),
            ("shopping" or "shop", "remove") => Remove("shopping", "Shopping", value),
            ("task" or "tasks", "add") => AddTask(value),
            ("task" or "tasks", "done") => await CompleteAsync("Task", "task.toggle", value, cancellationToken),
            ("task" or "tasks", "list") => await ListAsync("task", cancellationToken),
            ("task" or "tasks", "remove") => Remove("task", "Task", value),
            ("agenda" or "calendar", "add") => AddAgenda(value),
            ("agenda" or "calendar", "list") => await ListAsync("calendar", cancellationToken),
            ("agenda" or "calendar", "remove") => Remove("calendar", "Calendar", value),
            ("note" or "notes", "add") => AddNote(value),
            ("note" or "notes", "list") => await ListAsync("note", cancellationToken),
            ("note" or "notes", "remove") => Remove("note", "Note", value),
            ("package" or "delivery", "add") => AddPackage(value),
            ("package" or "delivery", "list") => await ListAsync("package", cancellationToken),
            ("package" or "delivery", "remove") => Remove("package", "Package", value),
            ("media" or "request", "add") => AddMedia(value, actor),
            ("media" or "request", "list") => await ListAsync("media", cancellationToken),
            ("media" or "request", "remove") => Remove("media", "Media", value),
            ("inbox" or "alert" or "alerts", "list") => await ListAsync("inbox", cancellationToken),
            ("inbox" or "alert" or "alerts", "ack") => await InboxActionAsync("notification.ack", value, cancellationToken),
            ("inbox" or "alert" or "alerts", "snooze") => await SnoozeAsync(value, cancellationToken),
            ("reminder" or "reminders", "add") => await AddReminderAsync(value, cancellationToken),
            ("automation" or "automations", "list") => await ListAsync("automation", cancellationToken),
            ("automation" or "automations", "run") => await RunAutomationAsync(value, cancellationToken),
            ("device" or "devices" or "home", "control") => await ControlDeviceAsync(value, cancellationToken),
            ("mode", "set") or ("system", "mode") => await SetModeAsync(value, cancellationToken),
            ("search", _) or ("system", "search") => Search(value),
            ("integration" or "integrations", "list") or ("system", "integrations") => await ListAsync("integration", cancellationToken),
            ("asset" or "assets", "list") or ("system", "assets") => await ListAsync("asset", cancellationToken),
            ("device" or "devices" or "home", "list") or ("system", "devices") => await ListAsync("device", cancellationToken),
            ("profile" or "profiles", "list") or ("system", "profiles") => await ListAsync("profile", cancellationToken),
            ("activity", "list") or ("system", "activity") => await ListAsync("activity", cancellationToken),
            ("system", "status") => await StatusAsync(cancellationToken),
            ("help", _) => Help(),
            _ => $"Unknown command.\n{Help()}"
        };
    }

    private string AddShopping(string value)
    {
        var fields = Fields(value);
        var names = fields[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(30).ToArray();
        if (names.Length == 0) return "Provide at least one shopping item.";
        foreach (var name in names)
            commandCenter.Upsert(new CommandCenterItemRequest("shopping", null, name, Category: fields.ElementAtOrDefault(1) ?? "Shopping"));
        return $"Added {names.Length} item{(names.Length == 1 ? "" : "s")} to {fields.ElementAtOrDefault(1) ?? "Shopping"}.";
    }

    private string AddTask(string value)
    {
        var fields = Fields(value);
        var dueAt = ParseDate(fields.ElementAtOrDefault(1));
        var priority = fields.ElementAtOrDefault(2) ?? "Normal";
        commandCenter.Upsert(new CommandCenterItemRequest("task", null, fields[0], Category: fields.ElementAtOrDefault(3) ?? "Inbox", Date: dueAt,
            Fields: new Dictionary<string, string> { ["priority"] = priority }));
        return $"Task added: {fields[0]}{(dueAt is null ? "" : $" for {dueAt.Value.LocalDateTime:g}")}.";
    }

    private string AddAgenda(string value)
    {
        var fields = Fields(value);
        var startsAt = ParseDate(fields.ElementAtOrDefault(1));
        if (startsAt is null) return "Agenda format: `agenda add Title | date and time | optional location`.";
        commandCenter.Upsert(new CommandCenterItemRequest("calendar", null, fields[0], Category: "Personal", Date: startsAt,
            Fields: new Dictionary<string, string> { ["location"] = fields.ElementAtOrDefault(2) ?? "" }));
        return $"Agenda entry added: {fields[0]} at {startsAt.Value.LocalDateTime:g}.";
    }

    private string AddNote(string value)
    {
        var fields = Fields(value);
        commandCenter.Upsert(new CommandCenterItemRequest("note", null, fields[0], fields.ElementAtOrDefault(1) ?? ""));
        return $"Note saved: {fields[0]}.";
    }

    private string AddPackage(string value)
    {
        var fields = Fields(value);
        commandCenter.Upsert(new CommandCenterItemRequest("package", null, fields[0], Date: ParseDate(fields.ElementAtOrDefault(3)),
            Fields: new Dictionary<string, string>
            {
                ["carrier"] = fields.ElementAtOrDefault(1) ?? "Carrier",
                ["trackingNumber"] = fields.ElementAtOrDefault(2) ?? "",
                ["status"] = "Tracking"
            }));
        return $"Package tracked: {fields[0]}.";
    }

    private string AddMedia(string value, string actor)
    {
        var fields = Fields(value);
        commandCenter.Upsert(new CommandCenterItemRequest("media", null, fields[0], Fields: new Dictionary<string, string>
        {
            ["mediaType"] = fields.ElementAtOrDefault(1) ?? "Media",
            ["requestedBy"] = actor,
            ["status"] = "Requested"
        }));
        return $"Media request added: {fields[0]}.";
    }

    private async Task<string> CompleteAsync(string kind, string tool, string query, CancellationToken cancellationToken)
    {
        var match = Resolve(kind, query);
        if (match is null) return $"No matching {kind.ToLowerInvariant()} item was found.";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest(tool, match.Id, true,
            tool == "task.toggle" ? new Dictionary<string, string> { ["completed"] = "true" } : null), cancellationToken);
        return result.Succeeded ? $"Completed: {match.Title}." : result.Message;
    }

    private string Remove(string storageKind, string displayKind, string query)
    {
        var match = Resolve(displayKind, query);
        if (match is null) return $"No matching {displayKind.ToLowerInvariant()} item was found.";
        return commandCenter.Delete(storageKind, match.Id) ? $"Removed: {match.Title}." : $"Could not remove {match.Title}.";
    }

    private async Task<string> InboxActionAsync(string tool, string query, CancellationToken cancellationToken)
    {
        var match = ResolveChoice("inbox", query);
        if (match is null) return "No matching alert was found.";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest(tool, match.Id, true), cancellationToken);
        return result.Succeeded ? $"Acknowledged: {match.Title}." : result.Message;
    }

    private async Task<string> SnoozeAsync(string value, CancellationToken cancellationToken)
    {
        var fields = Fields(value);
        var match = ResolveChoice("inbox", fields[0]);
        if (match is null) return "No matching alert was found.";
        var minutes = int.TryParse(fields.ElementAtOrDefault(1), out var parsed) ? Math.Clamp(parsed, 5, 10080) : 60;
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("notification.snooze", match.Id, true,
            new Dictionary<string, string> { ["minutes"] = minutes.ToString() }), cancellationToken);
        return result.Succeeded ? $"Snoozed {match.Title} for {minutes} minutes." : result.Message;
    }

    private async Task<string> SetModeAsync(string mode, CancellationToken cancellationToken)
    {
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("mode.set", mode.Trim(), true), cancellationToken);
        return result.Message;
    }

    private async Task<string> AddReminderAsync(string value, CancellationToken cancellationToken)
    {
        var fields = Fields(value);
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("notification.create", fields[0], true,
            new Dictionary<string, string> { ["message"] = fields.ElementAtOrDefault(1) ?? "Reminder created from Discord." }), cancellationToken);
        return result.Message;
    }

    private async Task<string> RunAutomationAsync(string query, CancellationToken cancellationToken)
    {
        var match = ResolveChoice("automation", query);
        if (match is null) return "No matching automation was found.";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("automation.run", match.Id, true), cancellationToken);
        return result.Message;
    }

    private async Task<string> ControlDeviceAsync(string value, CancellationToken cancellationToken)
    {
        var fields = Fields(value);
        if (!bool.TryParse(fields.ElementAtOrDefault(2), out var confirmed) || !confirmed)
            return "Device control was not run because confirmation was not provided.";
        var match = ResolveChoice("device", fields[0]);
        if (match is null) return "No matching Home Assistant device was found.";
        var domain = match.Id.Split('.', 2)[0];
        var service = fields.ElementAtOrDefault(1) is "turn_on" or "turn_off" ? fields[1] : "toggle";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("homeassistant.call", match.Id, true,
            new Dictionary<string, string> { ["domain"] = domain, ["service"] = service }), cancellationToken);
        return result.Message;
    }

    private string Search(string query)
    {
        var results = commandCenter.Search(query).Take(10).ToArray();
        return results.Length == 0 ? "No command-center matches found." : "**Search results**\n" + string.Join("\n", results.Select(item => $"- **{item.Title}** - {item.Kind}{Suffix(item.Subtitle)}"));
    }

    private async Task<string> ListAsync(string kind, CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        var lines = kind switch
        {
            "task" => snapshot.Tasks.Where(item => !item.Completed).Take(15).Select(item => $"- **{item.Title}** - {item.Priority}{(item.DueAt is null ? "" : $", due {item.DueAt.Value.LocalDateTime:g}")}"),
            "shopping" => snapshot.Shopping.Where(item => !item.Completed).Take(20).Select(item => $"- {item.Name}{(item.Quantity > 1 ? $" x{item.Quantity}" : "")} - {item.List}"),
            "calendar" => snapshot.Calendar.Take(15).Select(item => $"- **{item.Title}** - {item.StartsAt.LocalDateTime:g}{Suffix(item.Location)}"),
            "note" => snapshot.Notes.Take(15).Select(item => $"- **{item.Title}**{Suffix(Short(item.Body, 80))}"),
            "package" => snapshot.Packages.Take(15).Select(item => $"- **{item.Description}** - {item.Carrier}, {item.Status}"),
            "media" => snapshot.MediaRequests.Take(15).Select(item => $"- **{item.Title}** - {item.MediaType}, {item.Status}"),
            "inbox" => snapshot.Inbox.Where(item => !item.Acknowledged).Take(15).Select(item => $"- **{item.Title}** - {item.Source}: {Short(item.Message, 100)}"),
            "integration" => snapshot.Integrations.Where(item => item.Enabled).Take(20).Select(item => $"- {(item.Connected ? "Online" : "Offline")}: **{item.Name}** - {Short(item.Status, 90)}"),
            "asset" => snapshot.Assets.Where(item => !IsHealthy(item.Status)).Take(20).Select(item => $"- **{item.Name}** - {item.Status}{Suffix(item.Detail)}"),
            "device" => snapshot.HomeEntities.Take(20).Select(item => $"- **{item.Name}** - {item.State}{Suffix(item.Area)}"),
            "automation" => snapshot.Automations.Where(item => item.Enabled).Take(20).Select(item => $"- **{item.Name}** - {item.Trigger}{Suffix(item.LastResult)}"),
            "profile" => snapshot.Profiles.Where(item => item.Active).Take(20).Select(item => $"- **{item.DisplayName}** - {item.Role}"),
            "activity" => snapshot.Activity.Take(15).Select(item => $"- **{item.Tool}** - {Short(item.Message, 100)} ({item.OccurredAt.LocalDateTime:g})"),
            _ => []
        };
        var values = lines.ToArray();
        return values.Length == 0 ? $"No {kind} items to show." : $"**{char.ToUpperInvariant(kind[0]) + kind[1..]}**\n{string.Join("\n", values)}";
    }

    private CommandCenterSearchResult? Resolve(string kind, string query)
    {
        var byId = Autocomplete(kind.ToLowerInvariant(), "").FirstOrDefault(item => item.Id == query);
        if (byId is not null) return new CommandCenterSearchResult(byId.Id, kind, byId.Title, byId.Subtitle, null, 1);
        return commandCenter.Search(query).Where(item => item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Score).FirstOrDefault();
    }

    private DiscordCommandChoice? ResolveChoice(string area, string query) =>
        Autocomplete(area, "").FirstOrDefault(item => item.Id == query)
        ?? Autocomplete(area, query).FirstOrDefault();

    private async Task<string> StatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        return $"{snapshot.Tasks.Count(item => !item.Completed)} open tasks, {snapshot.Shopping.Count(item => !item.Completed)} shopping items, "
            + $"{snapshot.Calendar.Count} upcoming agenda entries, and {snapshot.Inbox.Count(item => !item.Acknowledged)} unread alerts.";
    }

    private static bool IsHealthy(string status) => status.Equals("online", StringComparison.OrdinalIgnoreCase)
        || status.Equals("healthy", StringComparison.OrdinalIgnoreCase) || status.Equals("ok", StringComparison.OrdinalIgnoreCase)
        || status.Equals("running", StringComparison.OrdinalIgnoreCase) || status.Equals("connected", StringComparison.OrdinalIgnoreCase);
    private static string Suffix(string? value) => string.IsNullOrWhiteSpace(value) ? "" : $" - {value}";
    private static string Short(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";
    private static string[] Fields(string value) => value.Split('|', StringSplitOptions.TrimEntries);
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static string Help() => "**HomeDashboard Discord commands**\nUse `/home` for guided commands and autocomplete. Prefix commands also work:\n"
        + "`!hd shopping add milk, bread | Groceries`\n"
        + "`!hd shopping list|done|remove ...`\n"
        + "`!hd shopping done milk`\n"
        + "`!hd task add Renew certificate | 2026-09-01 18:00 | High | Home`\n"
        + "`!hd task list|done|remove ...`\n"
        + "`!hd agenda add Dentist | 2026-09-03 14:00 | Downtown`\n"
        + "`!hd note add Project idea | Details`\n"
        + "`!hd package add Keyboard | UPS | 1Z... | 2026-09-04`\n"
        + "`!hd media add Dune Part Two | Movie`\n"
        + "`!hd inbox list|ack|snooze ...`\n"
        + "`!hd reminder add Title | Details` · `!hd automations list|run ...`\n"
        + "`!hd device list|control Entity | toggle | true`\n"
        + "`!hd mode set Away` · `!hd search query` · `!hd integrations list`\n"
        + "`!hd assets list` · `!hd profiles list` · `!hd activity list` · `!hd status`";
}
