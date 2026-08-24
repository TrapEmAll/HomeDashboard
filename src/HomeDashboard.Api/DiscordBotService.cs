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
        socket.Ready -= HandleReadyAsync;
        socket.Disconnected -= HandleDisconnectedAsync;
        socket.Log -= HandleLogAsync;
        try { await socket.StopAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Discord socket stop failed"); }
        try { await socket.LogoutAsync(); } catch (Exception ex) { logger.LogDebug(ex, "Discord logout failed"); }
        socket.Dispose();
    }

    private Task HandleReadyAsync()
    {
        commandCenter.SetIntegrationConnection("discord", true, $"Connected as {client?.CurrentUser.Username ?? "HomeDashboard"}");
        return Task.CompletedTask;
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

    private static bool IsAuthorized(SocketUserMessage message, DiscordBotConfiguration configuration)
    {
        if (!configuration.AllowedUserIds.Contains(message.Author.Id)) return false;
        if (configuration.AllowedChannelIds.Count > 0 && !configuration.AllowedChannelIds.Contains(message.Channel.Id)) return false;
        if (message.Channel is SocketGuildChannel guildChannel && configuration.AllowedGuildIds.Count > 0
            && !configuration.AllowedGuildIds.Contains(guildChannel.Guild.Id)) return false;
        return message.Channel is SocketGuildChannel || configuration.AllowedGuildIds.Count == 0;
    }

    private static string Fingerprint(DiscordBotConfiguration configuration)
    {
        var value = string.Join('|', configuration.Token, configuration.Prefix,
            string.Join(',', configuration.AllowedUserIds.Order()), string.Join(',', configuration.AllowedChannelIds.Order()),
            string.Join(',', configuration.AllowedGuildIds.Order()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}

public sealed class DiscordCommandProcessor(ICommandCenterService commandCenter)
{
    public async Task<string> ProcessAsync(string command, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Equals("help", StringComparison.OrdinalIgnoreCase)) return Help();
        var parts = command.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var area = parts[0].ToLowerInvariant();
        if (area == "status") return await StatusAsync(cancellationToken);
        if (parts.Length < 3) return $"Missing command details.\n{Help()}";

        var action = parts[1].ToLowerInvariant();
        var value = parts[2].Trim();
        return (area, action) switch
        {
            ("shopping" or "shop", "add") => AddShopping(value),
            ("shopping" or "shop", "done") => await CompleteAsync("Shopping", "shopping.toggle", value, cancellationToken),
            ("task" or "tasks", "add") => AddTask(value),
            ("task" or "tasks", "done") => await CompleteAsync("Task", "task.toggle", value, cancellationToken),
            ("agenda" or "calendar", "add") => AddAgenda(value),
            ("note" or "notes", "add") => AddNote(value),
            ("package" or "delivery", "add") => AddPackage(value),
            ("media" or "request", "add") => AddMedia(value, actor),
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
        var match = commandCenter.Search(query).Where(item => item.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Score).FirstOrDefault();
        if (match is null) return $"No matching {kind.ToLowerInvariant()} item was found.";
        var result = await commandCenter.ExecuteAsync(new CommandCenterActionRequest(tool, match.Id, true,
            tool == "task.toggle" ? new Dictionary<string, string> { ["completed"] = "true" } : null), cancellationToken);
        return result.Succeeded ? $"Completed: {match.Title}." : result.Message;
    }

    private async Task<string> StatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        return $"{snapshot.Tasks.Count(item => !item.Completed)} open tasks, {snapshot.Shopping.Count(item => !item.Completed)} shopping items, "
            + $"{snapshot.Calendar.Count} upcoming agenda entries, and {snapshot.Inbox.Count(item => !item.Acknowledged)} unread alerts.";
    }

    private static string[] Fields(string value) => value.Split('|', StringSplitOptions.TrimEntries);
    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    private static string Help() => "**HomeDashboard commands**\n"
        + "`!hd shopping add milk, bread | Groceries`\n"
        + "`!hd shopping done milk`\n"
        + "`!hd task add Renew certificate | 2026-09-01 18:00 | High | Home`\n"
        + "`!hd task done renew certificate`\n"
        + "`!hd agenda add Dentist | 2026-09-03 14:00 | Downtown`\n"
        + "`!hd note add Project idea | Details`\n"
        + "`!hd package add Keyboard | UPS | 1Z... | 2026-09-04`\n"
        + "`!hd media add Dune Part Two | Movie`\n"
        + "`!hd status`";
}
