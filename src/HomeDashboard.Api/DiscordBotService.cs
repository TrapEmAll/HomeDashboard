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
        socket.SelectMenuExecuted += HandleSelectMenuAsync;
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
        socket.SelectMenuExecuted -= HandleSelectMenuAsync;
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
                    var registeredGuilds = 0;
                    foreach (var guildId in configuration.AllowedGuildIds)
                    {
                        var guild = socket.GetGuild(guildId);
                        if (guild is null)
                        {
                            logger.LogWarning("Discord bot is not a member of configured guild {GuildId}", guildId);
                            continue;
                        }
                        await guild.BulkOverwriteApplicationCommandAsync([command]);
                        registeredGuilds++;
                    }
                    if (registeredGuilds == 0)
                        throw new InvalidOperationException("None of the configured Discord server IDs belongs to a server containing this bot.");
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
            if (response.StartsWith(DiscordCommandProcessor.AmbiguousMediaMessage, StringComparison.Ordinal)
                && MediaAddQuery(command) is { } query)
            {
                var choices = await processor.SearchMediaChoicesAsync(query, CancellationToken.None);
                if (choices.Count > 1)
                {
                    var menu = new SelectMenuBuilder()
                        .WithCustomId($"hd:media:{message.Author.Id}")
                        .WithPlaceholder("Choose the exact Sonarr or Radarr release")
                        .WithMinValues(1)
                        .WithMaxValues(1);
                    foreach (var choice in choices.Take(25))
                        menu.AddOption(LimitChoice(choice.Title, null), choice.Id, LimitChoice(choice.Subtitle ?? "Verified release", null));
                    var components = new ComponentBuilder().WithSelectMenu(menu).Build();
                    await message.Channel.SendMessageAsync("Several releases match. Choose the exact title below:", components: components,
                        allowedMentions: AllowedMentions.None);
                    return;
                }
            }
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
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(2400));
            var results = (await processor.AutocompleteAsync(path.Area, interaction.Data.Current.Name, query, timeout.Token))
                .Take(25).Select(item => new AutocompleteResult(LimitChoice(item.Title, item.Subtitle), item.Id));
            await interaction.RespondAsync(results);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Discord autocomplete failed for user {UserId}", interaction.User.Id);
            if (!interaction.HasResponded) await interaction.RespondAsync([]);
        }
    }

    private async Task HandleSelectMenuAsync(SocketMessageComponent interaction)
    {
        var configuration = activeConfiguration;
        if (configuration is null || !interaction.Data.CustomId.StartsWith("hd:media:", StringComparison.Ordinal)) return;
        if (!IsAuthorized(interaction, configuration))
        {
            await interaction.RespondAsync("This HomeDashboard selection is not available to your Discord account or channel.", ephemeral: true);
            return;
        }
        var owner = interaction.Data.CustomId["hd:media:".Length..];
        if (!owner.Equals(interaction.User.Id.ToString(), StringComparison.Ordinal))
        {
            await interaction.RespondAsync("Only the person who started this request can choose its release.", ephemeral: true);
            return;
        }

        try
        {
            var selectionId = interaction.Data.Values.FirstOrDefault();
            var response = string.IsNullOrWhiteSpace(selectionId)
                ? "No release was selected."
                : await processor.AddSelectedMediaAsync(selectionId, interaction.User.Username, CancellationToken.None);
            await interaction.UpdateAsync(message =>
            {
                message.Content = LimitMessage(response);
                message.Components = new ComponentBuilder().Build();
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord media selection failed for user {UserId}", interaction.User.Id);
            if (!interaction.HasResponded)
                await interaction.RespondAsync("The media request could not be completed. Search again and choose a current result.", ephemeral: true);
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
                "media_title" => "media",
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

    private static string? MediaAddQuery(string command)
    {
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3 || !(parts[0].Equals("media", StringComparison.OrdinalIgnoreCase)
            || parts[0].Equals("request", StringComparison.OrdinalIgnoreCase)) || !parts[1].Equals("add", StringComparison.OrdinalIgnoreCase)) return null;
        return parts[2].Split('|', 2, StringSplitOptions.TrimEntries)[0];
    }

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

public sealed class DiscordCommandProcessor(ICommandCenterService commandCenter, IArrMediaRequestService? arrMedia = null)
{
    public const string AmbiguousMediaMessage = "Several releases match that text.";
    public async Task<string> ProcessAsync(string command, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Equals("help", StringComparison.OrdinalIgnoreCase)) return Help();
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var area = parts[0].ToLowerInvariant();
        if (area == "status") return await StatusAsync(cancellationToken);
        if (area == "brief") return await BriefingAsync(cancellationToken);
        if (area == "attention") return await AttentionAsync(cancellationToken);
        if (area == "ask") return await AskAssistantAsync(command[parts[0].Length..].Trim(), cancellationToken);
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
            ("task", "defer") => string.Join(" | ", Item(), Get("days", "1")),
            ("task", "priority") => string.Join(" | ", Item(), Get("priority", "Normal")),
            ("task", "move") => string.Join(" | ", Item(), Get("list", "Inbox")),
            ("shopping", "add") => string.Join(" | ", Get("items"), Get("list", "Shopping")),
            ("shopping", "buy_all") => Get("list"),
            ("shopping", "clear_purchased") => Get("list"),
            ("calendar", "add") => string.Join(" | ", Get("title"), Get("when"), Get("location")),
            ("note", "add") => string.Join(" | ", Get("title"), Get("body")),
            ("note", "search") => Get("query"),
            ("note", "pin") => string.Join(" | ", Item(), Get("pinned", "true")),
            ("package", "add") => string.Join(" | ", Get("description"), Get("carrier", "Carrier"), Get("tracking"), Get("eta")),
            ("package", "update") => string.Join(" | ", Item(), Get("status"), Get("eta")),
            ("media", "search") => Get("query"),
            ("media", "add") => string.Join(" | ", Get("media_title", Get("title")), Get("search_now", "true")),
            ("system", "search") => Get("query"),
            ("system", "mode") => Get("mode", "Home"),
            ("inbox", "snooze") => string.Join(" | ", Item(), Get("minutes", "60")),
            ("notify", "send") => string.Join(" | ", Get("title"), Get("message")),
            ("reminder", "add") => string.Join(" | ", Get("title"), Get("message")),
            ("automation", "add") => string.Join(" | ", Get("name"), Get("trigger"), Get("action_tool"), Get("action_target")),
            ("assistant", "ask") => Get("question"),
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

    public async Task<IReadOnlyList<DiscordCommandChoice>> AutocompleteAsync(string area, string optionName, string query,
        CancellationToken cancellationToken)
    {
        if (area.Equals("media", StringComparison.OrdinalIgnoreCase) && optionName.Equals("media_title", StringComparison.OrdinalIgnoreCase))
        {
            if (arrMedia is null || query.Trim().Length < 2) return [];
            var results = await arrMedia.SearchAsync(query, cancellationToken);
            return results.Select(item => new DiscordCommandChoice(item.SelectionId, $"{item.Title}{Year(item.Year)}",
                $"{item.MediaType} · {item.Source}{(item.ImdbId is null ? "" : $" · {item.ImdbId}")}" )).Take(25).ToArray();
        }
        return Autocomplete(area, query);
    }

    public async Task<IReadOnlyList<DiscordCommandChoice>> SearchMediaChoicesAsync(string query, CancellationToken cancellationToken)
    {
        if (arrMedia is null || query.Trim().Length < 2) return [];
        var results = await arrMedia.SearchAsync(query, cancellationToken);
        return results.Select(item => new DiscordCommandChoice(item.SelectionId, $"{item.Title}{Year(item.Year)}",
            $"{item.MediaType} · {item.Source}{(item.ImdbId is null ? "" : $" · {item.ImdbId}")}" )).Take(25).ToArray();
    }

    public Task<string> AddSelectedMediaAsync(string selectionId, string actor, CancellationToken cancellationToken) =>
        AddMediaAsync($"{selectionId} | true", actor, cancellationToken);

    private async Task<string> ProcessCoreAsync(string area, string action, string value, string actor, CancellationToken cancellationToken)
    {
        return (area, action) switch
        {
            ("shopping" or "shop", "add") => AddShopping(value),
            ("shopping" or "shop", "done") => await CompleteAsync("Shopping", "shopping.toggle", value, cancellationToken),
            ("shopping" or "shop", "list") => await ListAsync("shopping", cancellationToken),
            ("shopping" or "shop", "purchased") => await ListAsync("shopping.purchased", cancellationToken),
            ("shopping" or "shop", "buy_all") => BuyAllShopping(value),
            ("shopping" or "shop", "clear_purchased") => ClearPurchasedShopping(value),
            ("shopping" or "shop", "remove") => Remove("shopping", "Shopping", value),
            ("task" or "tasks", "add") => AddTask(value),
            ("task" or "tasks", "done") => await CompleteAsync("Task", "task.toggle", value, cancellationToken),
            ("task" or "tasks", "reopen") => await ReopenTaskAsync(value, cancellationToken),
            ("task" or "tasks", "defer") => DeferTask(value),
            ("task" or "tasks", "priority") => UpdateTaskPriority(value),
            ("task" or "tasks", "move") => MoveTask(value),
            ("task" or "tasks", "list") => await ListAsync("task", cancellationToken),
            ("task" or "tasks", "today") => await ListAsync("task.today", cancellationToken),
            ("task" or "tasks", "overdue") => await ListAsync("task.overdue", cancellationToken),
            ("task" or "tasks", "remove") => Remove("task", "Task", value),
            ("agenda" or "calendar", "add") => AddAgenda(value),
            ("agenda" or "calendar", "list") => await ListAsync("calendar", cancellationToken),
            ("agenda" or "calendar", "today") => await ListAsync("calendar.today", cancellationToken),
            ("agenda" or "calendar", "week") => await ListAsync("calendar.week", cancellationToken),
            ("agenda" or "calendar", "remove") => Remove("calendar", "Calendar", value),
            ("note" or "notes", "add") => AddNote(value),
            ("note" or "notes", "list") => await ListAsync("note", cancellationToken),
            ("note" or "notes", "search") => SearchNotes(value),
            ("note" or "notes", "pin") => PinNote(value),
            ("note" or "notes", "remove") => Remove("note", "Note", value),
            ("package" or "delivery", "add") => AddPackage(value),
            ("package" or "delivery", "list") => await ListAsync("package", cancellationToken),
            ("package" or "delivery", "update") => UpdatePackage(value),
            ("package" or "delivery", "remove") => Remove("package", "Package", value),
            ("media" or "request", "search") => await SearchMediaAsync(value, cancellationToken),
            ("media" or "request", "add") => await AddMediaAsync(value, actor, cancellationToken),
            ("media" or "request", "list") => await ListAsync("media", cancellationToken),
            ("media" or "request", "remove") => Remove("media", "Media", value),
            ("inbox" or "alert" or "alerts", "list") => await ListAsync("inbox", cancellationToken),
            ("inbox" or "alert" or "alerts", "ack") => await InboxActionAsync("notification.ack", value, cancellationToken),
            ("inbox" or "alert" or "alerts", "ack_all") => AcknowledgeAllAlerts(),
            ("inbox" or "alert" or "alerts", "snooze") => await SnoozeAsync(value, cancellationToken),
            ("notify" or "notification", "send") => await SendNotificationAsync(value, cancellationToken),
            ("reminder" or "reminders", "add") => await AddReminderAsync(value, cancellationToken),
            ("automation" or "automations", "list") => await ListAsync("automation", cancellationToken),
            ("automation" or "automations", "add") => AddAutomation(value),
            ("automation" or "automations", "run") => await RunAutomationAsync(value, cancellationToken),
            ("assistant" or "ask", "ask") => await AskAssistantAsync(value, cancellationToken),
            ("assistant", "brief") => await BriefingAsync(cancellationToken),
            ("assistant", "attention") => await AttentionAsync(cancellationToken),
            ("device" or "devices" or "home", "control") => await ControlDeviceAsync(value, cancellationToken),
            ("mode", "set") or ("system", "mode") => await SetModeAsync(value, cancellationToken),
            ("search", _) or ("system", "search") => Search(value),
            ("system", "brief") => await BriefingAsync(cancellationToken),
            ("system", "attention") => await AttentionAsync(cancellationToken),
            ("integration" or "integrations", "list") or ("system", "integrations") => await ListAsync("integration", cancellationToken),
            ("asset" or "assets", "list") or ("system", "assets") => await ListAsync("asset", cancellationToken),
            ("device" or "devices" or "home", "list") or ("system", "devices") => await ListAsync("device", cancellationToken),
            ("profile" or "profiles", "list") or ("system", "profiles") => await ListAsync("profile", cancellationToken),
            ("activity", "list") or ("system", "activity") => await ListAsync("activity", cancellationToken),
            ("system", "logs") => await LogsAsync(cancellationToken),
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

    private async Task<string> ReopenTaskAsync(string query, CancellationToken cancellationToken)
    {
        var match = Resolve("Task", query);
        if (match is null) return "No matching task was found.";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("task.toggle", match.Id, true,
            new Dictionary<string, string> { ["completed"] = "false" }), cancellationToken);
        return result.Succeeded ? $"Reopened: {match.Title}." : result.Message;
    }

    private string DeferTask(string value)
    {
        var fields = Fields(value);
        var task = FindTask(fields[0]);
        if (task is null) return "No matching task was found.";
        var days = int.TryParse(fields.ElementAtOrDefault(1), out var parsed) ? Math.Clamp(parsed, 1, 365) : 1;
        var due = (task.DueAt ?? DateTimeOffset.Now).AddDays(days);
        commandCenter.Upsert(TaskRequest(task with { DueAt = due }));
        return $"Deferred {task.Title} to {due.LocalDateTime:g}.";
    }

    private string UpdateTaskPriority(string value)
    {
        var fields = Fields(value);
        var task = FindTask(fields[0]);
        if (task is null) return "No matching task was found.";
        var priority = Enum.TryParse<ItemPriority>(fields.ElementAtOrDefault(1), true, out var parsed) ? parsed : ItemPriority.Normal;
        commandCenter.Upsert(TaskRequest(task with { Priority = priority }));
        return $"{task.Title} is now {priority} priority.";
    }

    private string MoveTask(string value)
    {
        var fields = Fields(value);
        var task = FindTask(fields[0]);
        if (task is null) return "No matching task was found.";
        var list = string.IsNullOrWhiteSpace(fields.ElementAtOrDefault(1)) ? "Inbox" : fields[1].Trim();
        commandCenter.Upsert(TaskRequest(task with { List = list }));
        return $"Moved {task.Title} to {list}.";
    }

    private string BuyAllShopping(string list)
    {
        var archive = commandCenter.Export();
        var items = archive.Shopping.Where(item => !item.Completed && MatchesList(item.List, list)).Take(500).ToArray();
        if (items.Length == 0) return "No matching unpurchased shopping items were found.";
        commandCenter.ApplyBatch(new CommandCenterBatchRequest(items.Select(item =>
            new CommandCenterActionRequest("shopping.toggle", item.Id)).ToArray()));
        return $"Marked {items.Length} shopping item{(items.Length == 1 ? "" : "s")} purchased.";
    }

    private string ClearPurchasedShopping(string list)
    {
        var archive = commandCenter.Export();
        var items = archive.Shopping.Where(item => item.Completed && MatchesList(item.List, list)).Take(500).ToArray();
        if (items.Length == 0) return "No matching purchased shopping items were found.";
        commandCenter.ApplyBatch(new CommandCenterBatchRequest(Deletes: items.Select(item =>
            new CommandCenterDeleteRequest("shopping", item.Id)).ToArray()));
        return $"Cleared {items.Length} purchased shopping item{(items.Length == 1 ? "" : "s")}.";
    }

    private string SearchNotes(string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return "Provide something to search for.";
        var archive = commandCenter.Export();
        var notes = archive.Notes.Where(item => item.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.Body.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || item.Tags.Any(tag => tag.Contains(needle, StringComparison.OrdinalIgnoreCase))).Take(15).ToArray();
        return notes.Length == 0 ? "No matching notes found." : "**Notes**\n" + string.Join("\n", notes.Select(item =>
            $"- **{item.Title}**{Suffix(Short(item.Body, 100))}"));
    }

    private string PinNote(string value)
    {
        var fields = Fields(value);
        var note = FindNote(fields[0]);
        if (note is null) return "No matching note was found.";
        var pinned = !bool.TryParse(fields.ElementAtOrDefault(1), out var parsed) || parsed;
        commandCenter.Upsert(new CommandCenterItemRequest("note", note.Id, note.Title, note.Body,
            Fields: new Dictionary<string, string>
            {
                ["tags"] = string.Join(", ", note.Tags),
                ["pinned"] = pinned.ToString()
            }));
        return pinned ? $"Pinned: {note.Title}." : $"Unpinned: {note.Title}.";
    }

    private string UpdatePackage(string value)
    {
        var fields = Fields(value);
        var package = FindPackage(fields[0]);
        if (package is null) return "No matching package was found.";
        var status = string.IsNullOrWhiteSpace(fields.ElementAtOrDefault(1)) ? package.Status : fields[1].Trim();
        var eta = ParseDate(fields.ElementAtOrDefault(2)) ?? package.EstimatedDelivery;
        commandCenter.Upsert(new CommandCenterItemRequest("package", package.Id, package.Description, Date: eta,
            Fields: new Dictionary<string, string>
            {
                ["carrier"] = package.Carrier,
                ["trackingNumber"] = package.TrackingNumber,
                ["status"] = status
            }));
        return $"Updated {package.Description}: {status}.";
    }

    private string AddAutomation(string value)
    {
        var fields = Fields(value);
        if (fields[0].Length == 0 || fields.ElementAtOrDefault(1)?.Length is null or 0 || fields.ElementAtOrDefault(2)?.Length is null or 0)
            return "Automation format: `automation add Name | daily at 08:00 | notification.create | Check dashboard`.";
        commandCenter.Upsert(new CommandCenterItemRequest("automation", null, fields[0], Fields: new Dictionary<string, string>
        {
            ["trigger"] = fields[1],
            ["actionTool"] = fields[2],
            ["actionTarget"] = fields.ElementAtOrDefault(3) ?? "",
            ["enabled"] = "true"
        }));
        return $"Automation created: {fields[0]}.";
    }

    private string AcknowledgeAllAlerts()
    {
        var archive = commandCenter.Export();
        var alerts = archive.Inbox.Where(item => !item.Acknowledged
            && (item.SnoozedUntil is null || item.SnoozedUntil <= DateTimeOffset.UtcNow)).Take(500).ToArray();
        if (alerts.Length == 0) return "No unread alerts to acknowledge.";
        commandCenter.ApplyBatch(new CommandCenterBatchRequest(alerts.Select(item =>
            new CommandCenterActionRequest("notification.ack", item.Id)).ToArray()));
        return $"Acknowledged {alerts.Length} alert{(alerts.Length == 1 ? "" : "s")}.";
    }

    private async Task<string> SearchMediaAsync(string value, CancellationToken cancellationToken)
    {
        if (arrMedia is null) return "Sonarr/Radarr media search is unavailable.";
        var results = await arrMedia.SearchAsync(value, cancellationToken);
        if (results.Count == 0) return "No matching Sonarr or Radarr releases were found. Check that the service URL and API key are configured.";
        return "**Sonarr/Radarr matches**\n" + string.Join("\n", results.Take(10).Select(item =>
            $"- **{item.Title}{Year(item.Year)}** - {item.MediaType} · {item.Source}{(item.ImdbId is null ? "" : $" · `{item.ImdbId}`")}"));
    }

    private async Task<string> AddMediaAsync(string value, string actor, CancellationToken cancellationToken)
    {
        if (arrMedia is null) return "Sonarr/Radarr media requests are unavailable.";
        var fields = Fields(value);
        var selection = fields[0];
        var searchNow = !bool.TryParse(fields.ElementAtOrDefault(1), out var parsed) || parsed;
        if (!selection.StartsWith("movie:", StringComparison.Ordinal) && !selection.StartsWith("series:", StringComparison.Ordinal))
        {
            var matches = await arrMedia.SearchAsync(selection, cancellationToken);
            var normalized = selection.Replace("imdb:", "", StringComparison.OrdinalIgnoreCase).Trim();
            var exact = matches.Where(item => item.Title.Equals(selection, StringComparison.OrdinalIgnoreCase)
                || item.ImdbId?.Equals(normalized, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (exact.Length == 1) selection = exact[0].SelectionId;
            else if (matches.Count == 1) selection = matches[0].SelectionId;
            else return matches.Count == 0 ? "No matching Sonarr or Radarr release was found."
                : $"{AmbiguousMediaMessage} Choose the correct title, year, and IMDb ID from the Discord menu.";
        }

        var result = await arrMedia.RequestAsync(selection, searchNow, cancellationToken);
        if (!result.Succeeded || result.Media is null) return result.Message;
        var media = result.Media;
        commandCenter.Upsert(new CommandCenterItemRequest("media", null, $"{media.Title}{Year(media.Year)}", Fields: new Dictionary<string, string>
        {
            ["mediaType"] = media.MediaType,
            ["requestedBy"] = actor,
            ["status"] = result.Status,
            ["artworkUrl"] = media.ArtworkUrl ?? "",
            ["imdbId"] = media.ImdbId ?? "",
            ["tmdbId"] = media.TmdbId?.ToString() ?? "",
            ["tvdbId"] = media.TvdbId?.ToString() ?? "",
            ["source"] = media.Source
        }));
        return result.Message;
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

    private async Task<string> SendNotificationAsync(string value, CancellationToken cancellationToken)
    {
        var fields = Fields(value);
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("notification.send", fields.ElementAtOrDefault(1) ?? fields[0], true,
            new Dictionary<string, string> { ["title"] = fields[0], ["message"] = fields.ElementAtOrDefault(1) ?? fields[0] }), cancellationToken);
        return result.Message;
    }

    private async Task<string> RunAutomationAsync(string query, CancellationToken cancellationToken)
    {
        var match = ResolveChoice("automation", query);
        if (match is null) return "No matching automation was found.";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest("automation.run", match.Id, true), cancellationToken);
        return result.Message;
    }

    private async Task<string> AskAssistantAsync(string question, CancellationToken cancellationToken)
    {
        var response = await commandCenter.AskAsync(new AssistantRequest(question, false), cancellationToken);
        var suggestions = response.Suggestions.Take(3).Select(item => $"- {item.Label}").ToArray();
        return suggestions.Length == 0 ? response.Message : $"{response.Message}\n\n**Suggestions**\n{string.Join("\n", suggestions)}";
    }

    private async Task<string> BriefingAsync(CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        return $"**{snapshot.Briefing.Greeting}**\n{snapshot.Briefing.Summary}\n" + string.Join("\n", snapshot.Briefing.Highlights.Select(item => $"- {item}"));
    }

    private async Task<string> AttentionAsync(CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        var alerts = snapshot.Inbox.Where(item => !item.Acknowledged && item.Severity != NotificationSeverity.Info).Take(5)
            .Select(item => $"- **{item.Title}** - {item.Source}: {Short(item.Message, 100)}");
        var tasks = snapshot.Tasks.Where(item => !item.Completed && item.DueAt < DateTimeOffset.UtcNow).Take(5)
            .Select(item => $"- **{item.Title}** - overdue task{(item.DueAt is null ? "" : $" due {item.DueAt.Value.LocalDateTime:g}")}");
        var assets = snapshot.Assets.Where(item => !IsHealthy(item.Status)).Take(5)
            .Select(item => $"- **{item.Name}** - {item.Status}{Suffix(item.Detail)}");
        var lines = alerts.Concat(tasks).Concat(assets).Take(15).ToArray();
        return lines.Length == 0 ? "Nothing currently needs attention." : $"**Needs attention**\n{string.Join("\n", lines)}";
    }

    private async Task<string> LogsAsync(CancellationToken cancellationToken)
    {
        var logs = await commandCenter.GetSystemLogsAsync(6, cancellationToken);
        return logs.Count == 0 ? "No recent API logs were available." : "**Recent logs**\n" + string.Join("\n", logs.Select(item =>
            $"- **{item.Level}** {item.Source}: {Short(item.Message, 120)}"));
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
        var today = DateTime.Today;
        var weekEnd = today.AddDays(7);
        var lines = kind switch
        {
            "task" => snapshot.Tasks.Where(item => !item.Completed).Take(15).Select(item => $"- **{item.Title}** - {item.Priority}{(item.DueAt is null ? "" : $", due {item.DueAt.Value.LocalDateTime:g}")}"),
            "task.today" => snapshot.Tasks.Where(item => !item.Completed && item.DueAt?.LocalDateTime.Date == today).Take(15).Select(item => $"- **{item.Title}** - {item.Priority}, due {item.DueAt!.Value.LocalDateTime:t}"),
            "task.overdue" => snapshot.Tasks.Where(item => !item.Completed && item.DueAt < DateTimeOffset.UtcNow).Take(15).Select(item => $"- **{item.Title}** - {item.Priority}, due {item.DueAt!.Value.LocalDateTime:g}"),
            "shopping" => snapshot.Shopping.Where(item => !item.Completed).Take(20).Select(item => $"- {item.Name}{(item.Quantity > 1 ? $" x{item.Quantity}" : "")} - {item.List}"),
            "shopping.purchased" => snapshot.Shopping.Where(item => item.Completed).Take(20).Select(item => $"- {item.Name}{(item.Quantity > 1 ? $" x{item.Quantity}" : "")} - {item.List}"),
            "calendar" => snapshot.Calendar.Take(15).Select(item => $"- **{item.Title}** - {item.StartsAt.LocalDateTime:g}{Suffix(item.Location)}"),
            "calendar.today" => snapshot.Calendar.Where(item => item.StartsAt.LocalDateTime.Date == today).Take(15).Select(item => $"- **{item.Title}** - {item.StartsAt.LocalDateTime:t}{Suffix(item.Location)}"),
            "calendar.week" => snapshot.Calendar.Where(item => item.StartsAt.LocalDateTime.Date >= today && item.StartsAt.LocalDateTime.Date < weekEnd).Take(25).Select(item => $"- **{item.Title}** - {item.StartsAt.LocalDateTime:ddd g}{Suffix(item.Location)}"),
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
        var title = kind.Contains('.') ? kind.Split('.')[0] : kind;
        return values.Length == 0 ? $"No {title} items to show." : $"**{char.ToUpperInvariant(title[0]) + title[1..]}**\n{string.Join("\n", values)}";
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

    private PersonalTask? FindTask(string query)
    {
        var archive = commandCenter.Export();
        var choice = ResolveChoice("task", query);
        return choice is null ? null : archive.Tasks.FirstOrDefault(item => item.Id == choice.Id);
    }

    private QuickNote? FindNote(string query)
    {
        var archive = commandCenter.Export();
        var choice = ResolveChoice("note", query);
        return choice is null ? null : archive.Notes.FirstOrDefault(item => item.Id == choice.Id);
    }

    private TrackedPackage? FindPackage(string query)
    {
        var archive = commandCenter.Export();
        var choice = ResolveChoice("package", query);
        return choice is null ? null : archive.Packages.FirstOrDefault(item => item.Id == choice.Id);
    }

    private async Task<string> StatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        return $"{snapshot.Tasks.Count(item => !item.Completed)} open tasks, {snapshot.Shopping.Count(item => !item.Completed)} shopping items, "
            + $"{snapshot.Calendar.Count} upcoming agenda entries, and {snapshot.Inbox.Count(item => !item.Acknowledged)} unread alerts.";
    }

    private static CommandCenterItemRequest TaskRequest(PersonalTask task) =>
        new("task", task.Id, task.Title, task.Details, task.List, task.DueAt, new Dictionary<string, string>
        {
            ["priority"] = task.Priority.ToString(),
            ["completed"] = task.Completed.ToString()
        });

    private static bool MatchesList(string itemList, string? filter) =>
        string.IsNullOrWhiteSpace(filter) || itemList.Equals(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool IsHealthy(string status) => status.Equals("online", StringComparison.OrdinalIgnoreCase)
        || status.Equals("healthy", StringComparison.OrdinalIgnoreCase) || status.Equals("ok", StringComparison.OrdinalIgnoreCase)
        || status.Equals("running", StringComparison.OrdinalIgnoreCase) || status.Equals("connected", StringComparison.OrdinalIgnoreCase);
    private static string Suffix(string? value) => string.IsNullOrWhiteSpace(value) ? "" : $" - {value}";
    private static string Short(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";
    private static string Year(int? year) => year is null ? "" : $" ({year})";
    private static string[] Fields(string value) => value.Split('|', StringSplitOptions.TrimEntries);
    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (DateTimeOffset.TryParse(trimmed, out var parsed)) return parsed;
        var lower = trimmed.ToLowerInvariant();
        var baseDate = lower.StartsWith("tomorrow") ? DateTime.Today.AddDays(1) : lower.StartsWith("today") ? DateTime.Today : (DateTime?)null;
        if (baseDate is null) return null;
        var timeText = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ElementAtOrDefault(1);
        var time = TimeSpan.Zero;
        if (!string.IsNullOrWhiteSpace(timeText) && TimeOnly.TryParse(timeText, out var parsedTime)) time = parsedTime.ToTimeSpan();
        return new DateTimeOffset(baseDate.Value.Add(time));
    }
    private static string Help() => "**HomeDashboard Discord commands**\nUse `/home` for guided commands and autocomplete. Prefix commands also work:\n"
        + "`!hd shopping add milk, bread | Groceries`\n"
        + "`!hd shopping list|purchased|done|buy_all|clear_purchased|remove ...`\n"
        + "`!hd task add Renew certificate | 2026-09-01 18:00 | High | Home`\n"
        + "`!hd task list|today|overdue|done|reopen|defer|priority|move|remove ...`\n"
        + "`!hd agenda add Dentist | 2026-09-03 14:00 | Downtown`\n"
        + "`!hd agenda list|today|week|remove ...`\n"
        + "`!hd note add Project idea | Details` · `!hd note search|pin|remove ...`\n"
        + "`!hd package add Keyboard | UPS | 1Z... | 2026-09-04`\n"
        + "`!hd package list|update|remove ...`\n"
        + "`!hd media search Dune` · use `/home media add` to select and submit a verified release\n"
        + "`!hd inbox list|ack|ack_all|snooze ...`\n"
        + "`!hd notify send Title | Message`\n"
        + "`!hd reminder add Title | Details` · `!hd automations list|run ...`\n"
        + "`!hd automation add Name | daily at 08:00 | notification.create | Check dashboard`\n"
        + "`!hd ask What needs attention?` · `!hd brief` · `!hd attention`\n"
        + "`!hd device list|control Entity | toggle | true`\n"
        + "`!hd mode set Away` · `!hd search query` · `!hd integrations list`\n"
        + "`!hd assets list` · `!hd profiles list` · `!hd activity list` · `!hd system logs` · `!hd status`";
}
