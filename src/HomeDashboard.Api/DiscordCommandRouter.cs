using System.Text.RegularExpressions;
using HomeDashboard.Contracts;

namespace HomeDashboard.Api;

public sealed class DiscordCommandRouter(
    DiscordCommandProcessor processor,
    IRestartCoordinator restarts,
    IServiceStatusProvider services,
    ISystemStatsProvider systemStats,
    ISetupService setup,
    IOperationsService operations,
    ICommandCenterService commandCenter,
    IDashboardService? dashboard = null,
    IAgentCommandStore? commandStore = null)
{
    public async Task<CommandCenterActionResult> ExecuteAsync(string command, string actor, ulong? discordUserId, CancellationToken cancellationToken)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return Fail("Missing command details.");
        var tokens = parts.Select(item => item.ToLowerInvariant()).ToArray();

        try
        {
            var authorization = await AuthorizeAsync(command, discordUserId, cancellationToken);
            if (authorization is not null) return authorization;
            if (tokens is ["restart", "service", ..])
                return await RestartServiceAsync(command["restart service".Length..].Trim(), actor, cancellationToken);
            if (tokens is ["backup", "now"])
                return BackupNow();
            if (tokens is ["restore", ..])
                return Fail("Restore requires a dashboard backup payload; Discord restore by id is not available yet.");
            if (tokens[0] == "maintenance")
                return AddMaintenance(command["maintenance".Length..].Trim(), actor);
            if (tokens[0] == "machine")
                return await QueueMachineAsync(parts, actor, cancellationToken);
            if (tokens[0] is "status" or "health")
                return await StatusAsync(cancellationToken);
            if (tokens is ["list", "rules"])
                return await ListAutomationRulesAsync(cancellationToken);
            if (tokens.Length >= 3 && tokens[1] == "rule" && (tokens[0] is "run" or "enable" or "disable"))
                return await AutomationCommandAsync(tokens[0], command[command.IndexOf(' ', StringComparison.Ordinal)..].Trim()["rule".Length..].Trim(), cancellationToken);

            if (tokens[0] == "home" || tokens[0] == "homeassistant")
            {
                var homeCommand = tokens[0] == "home" && tokens.ElementAtOrDefault(1) == "control"
                    ? command[(command.IndexOf(' ') + 1)..].Trim()["control".Length..].Trim()
                    : command;
                if (await TryHomeControlAsync(homeCommand, cancellationToken) is { } homeResult)
                    return homeResult;
            }

            if (await TryPersonalCommandAsync(parts, tokens, actor, cancellationToken) is { } personalResult)
                return personalResult;

            var message = await processor.ProcessAsync(command, actor, cancellationToken);
            var succeeded = !message.StartsWith("Unknown command.", StringComparison.OrdinalIgnoreCase);
            return new CommandCenterActionResult(succeeded, succeeded ? message : Help());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex.Message);
        }
    }

    public async Task<CommandCenterActionResult?> AuthorizeAsync(string command, ulong? discordUserId, CancellationToken cancellationToken)
    {
        var configuration = commandCenter.GetDiscordConfiguration();
        if (configuration is null || discordUserId is null || !configuration.ProfileMappings.TryGetValue(discordUserId.Value, out var profileId))
            return new CommandCenterActionResult(false, "Not authorized for HomeDashboard commands.");

        var profile = (await commandCenter.GetSnapshotAsync(cancellationToken)).Profiles
            .FirstOrDefault(item => item.Active && item.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return new CommandCenterActionResult(false, "Not authorized for HomeDashboard commands.");
        if (RequiresAdministrator(command) && !profile.Role.Equals("Administrator", StringComparison.OrdinalIgnoreCase))
            return new CommandCenterActionResult(false, "Administrator access is required for this command.");
        return null;
    }

    private async Task<CommandCenterActionResult> RestartServiceAsync(string query, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return Fail("Choose a service to restart.");
        var service = await ResolveServiceAsync(query, cancellationToken);
        if (service is null) return Fail($"No service matched '{query}'.");

        var result = restarts.QueueRestart(service.Id, new RestartRequest(actor, $"Discord restart request for {service.Name}", true));
        var succeeded = result.State == RestartState.Queued;
        return new CommandCenterActionResult(succeeded, result.Message, AuditId: result.CommandId);
    }

    private CommandCenterActionResult BackupNow()
    {
        var backup = new DashboardBackup(
            1,
            DateTimeOffset.UtcNow,
            setup.GetSettings(),
            operations.GetMaintenance(),
            typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            commandCenter.Export());
        return new CommandCenterActionResult(true,
            $"Backup snapshot created with {backup.Settings.Services.Count} services, {backup.Maintenance.Count} maintenance windows, and command-center data.");
    }

    private CommandCenterActionResult AddMaintenance(string title, string actor)
    {
        if (string.IsNullOrWhiteSpace(title)) return Fail("Provide a maintenance task.");
        var startsAt = DateTimeOffset.UtcNow;
        var window = operations.AddMaintenance(new CreateMaintenanceWindowRequest(title, startsAt, startsAt.AddHours(1), null, true), actor);
        return new CommandCenterActionResult(true, $"Maintenance queued: {window.Title} until {window.EndsAt.LocalDateTime:g}.");
    }

    private async Task<CommandCenterActionResult> QueueMachineAsync(string[] parts, string actor, CancellationToken cancellationToken)
    {
        if (parts.Length < 3) return Fail("Machine commands require an action and agent ID.");
        var tool = parts[1].ToLowerInvariant() switch
        {
            "lock" => "machine.lock",
            "sleep" => "machine.sleep",
            "restart" => "machine.restart",
            "shutdown" => "machine.shutdown",
            _ => null
        };
        if (tool is null) return Fail($"Unknown machine action '{parts[1]}'.");

        var agentId = parts[2];
        return await commandCenter.ExecuteAsync(new CommandCenterActionRequest(tool, agentId, true,
            new Dictionary<string, string> { ["requestedBy"] = actor }), cancellationToken);
    }

    private async Task<CommandCenterActionResult> StatusAsync(CancellationToken cancellationToken)
    {
        if (dashboard is null || commandStore is null)
            return await LegacyStatusAsync(cancellationToken);

        var snapshot = await dashboard.GetSnapshotAsync(cancellationToken);
        var healthy = snapshot.Services.Count(item => item.Status == ServiceStatus.Online);
        var degraded = snapshot.Services.Count(item => item.Status == ServiceStatus.Degraded);
        var offline = snapshot.Services.Count(item => item.Status == ServiceStatus.Offline);
        var allActiveAgents = snapshot.Agents.Where(item => item.Status == ServiceStatus.Online).ToArray();
        var activeAgents = allActiveAgents.Take(8).ToArray();
        var pendingCommands = commandStore.GetRecentCommands(50)
            .Where(item => item.State is AgentCommandState.Queued or AgentCommandState.Running)
            .ToArray();

        var lines = new List<string>
        {
            $"Services: {healthy} healthy, {degraded} degraded, {offline} offline.",
            "Agents: " + (activeAgents.Length == 0
                ? "none active."
                : string.Join(", ", activeAgents.Select(item => $"{item.AgentId} (last seen {item.LastSeenAt.LocalDateTime:g})"))
                    + (allActiveAgents.Length > activeAgents.Length ? $" +{allActiveAgents.Length - activeAgents.Length} more." : "."))
        };

        var alerts = snapshot.Notifications
            .Where(item => item.Severity != NotificationSeverity.Info)
            .Take(6)
            .Select(item => $"- {item.Severity}: {item.Title}")
            .ToArray();
        lines.Add(alerts.Length == 0 ? "Alerts: none unresolved." : "Alerts:\n" + string.Join("\n", alerts));

        var commands = pendingCommands.Take(6)
            .Select(item => $"- {item.State}: {item.Kind} on {item.AgentId}")
            .ToArray();
        lines.Add(commands.Length == 0 ? "Queue: no pending commands." : "Queue:\n" + string.Join("\n", commands));
        lines.Add("Details: open the HomeDashboard web dashboard.");

        return new CommandCenterActionResult(true, FitDiscordMessage(lines));
    }

    private async Task<CommandCenterActionResult> LegacyStatusAsync(CancellationToken cancellationToken)
    {
        var serviceCards = await services.GetServicesAsync(cancellationToken);
        var stats = systemStats.GetStats();
        var online = serviceCards.Count(item => item.Status == ServiceStatus.Online);
        var degraded = serviceCards.Count(item => item.Status == ServiceStatus.Degraded);
        var offline = serviceCards.Count(item => item.Status == ServiceStatus.Offline);
        return new CommandCenterActionResult(true,
            $"{online} online, {degraded} degraded, {offline} offline. {stats.Hostname}: CPU {stats.CpuPercent:0.#}%, memory {stats.MemoryUsedPercent:0.#}%.");
    }

    private async Task<CommandCenterActionResult?> TryHomeControlAsync(string command, CancellationToken cancellationToken)
    {
        var text = command.Trim();
        var match = Regex.Match(text, @"^(?<target>.+?)\s+(?<action>on|off|toggle)$", RegexOptions.IgnoreCase);
        var temperature = Regex.Match(text, @"^(?<target>.+?)\s+(?<temperature>\d{2,3})$", RegexOptions.IgnoreCase);
        var requestedAction = match.Success ? match.Groups["action"].Value.ToLowerInvariant() : null;
        var target = match.Success ? match.Groups["target"].Value : temperature.Success ? temperature.Groups["target"].Value : text;
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        var entity = ResolveHomeEntity(snapshot.HomeEntities, target, temperature.Success);

        if (entity is null)
            return new CommandCenterActionResult(false, HomeSuggestion(snapshot.HomeEntities, target));

        string service;
        var arguments = new Dictionary<string, string> { ["domain"] = entity.Domain };
        if (temperature.Success && entity.Domain.Equals("climate", StringComparison.OrdinalIgnoreCase))
        {
            service = "set_temperature";
            arguments["temperature"] = temperature.Groups["temperature"].Value;
        }
        else if (requestedAction is "on" or "off" or "toggle")
        {
            service = requestedAction == "on" ? "turn_on" : requestedAction == "off" ? "turn_off" : "toggle";
        }
        else
        {
            return new CommandCenterActionResult(false, HomeSuggestion(snapshot.HomeEntities, target, entity));
        }

        arguments["service"] = service;
        return await commandCenter.ExecuteAsync(new CommandCenterActionRequest(
            "homeassistant.call", entity.Id, true, arguments), cancellationToken);
    }

    private async Task<CommandCenterActionResult> ListAutomationRulesAsync(CancellationToken cancellationToken)
    {
        var rules = (await commandCenter.GetSnapshotAsync(cancellationToken)).Automations;
        if (rules.Count == 0) return new CommandCenterActionResult(true, "No automation rules are configured.");
        var lines = rules.Select(rule => $"- **{rule.Name}** · {rule.Trigger} · {(rule.Enabled ? "enabled" : "disabled")}");
        return new CommandCenterActionResult(true, FitDiscordMessage(["**Automation rules**", string.Join("\n", lines)]));
    }

    private async Task<CommandCenterActionResult> AutomationCommandAsync(string action, string query, CancellationToken cancellationToken)
    {
        var snapshot = await commandCenter.GetSnapshotAsync(cancellationToken);
        var rule = snapshot.Automations.FirstOrDefault(item => item.Id.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Automations.FirstOrDefault(item => item.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Automations.FirstOrDefault(item => item.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return new CommandCenterActionResult(false, $"No automation rule matched '{query}'.");

        if (action == "run")
            return await commandCenter.ExecuteAsync(new CommandCenterActionRequest("automation.run", rule.Id, true), cancellationToken);

        var enabled = action == "enable";
        commandCenter.Upsert(new CommandCenterItemRequest("automation", rule.Id, rule.Name,
            Fields: new Dictionary<string, string>
            {
                ["trigger"] = rule.Trigger,
                ["condition"] = rule.Condition ?? "",
                ["actionTool"] = rule.ActionTool,
                ["actionTarget"] = rule.ActionTarget ?? "",
                ["enabled"] = enabled.ToString()
            }));
        return new CommandCenterActionResult(true, $"Automation '{rule.Name}' {(enabled ? "enabled" : "disabled")}.");
    }

    private static HomeEntity? ResolveHomeEntity(IReadOnlyList<HomeEntity> entities, string target, bool climateOnly)
    {
        var terms = target.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeHomeTerm)
            .ToArray();
        return entities
            .Where(entity => !climateOnly || entity.Domain.Equals("climate", StringComparison.OrdinalIgnoreCase))
            .Select(entity => new { Entity = entity, Text = $"{entity.Id} {entity.Name} {entity.Domain} {entity.Area}".ToLowerInvariant() })
            .Where(item => terms.Length > 0 && terms.All(term => item.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Entity)
            .FirstOrDefault();
    }

    private static string HomeSuggestion(IReadOnlyList<HomeEntity> entities, string target, HomeEntity? entity = null)
    {
        if (entity is not null)
            return $"Did you mean `{entity.Name} toggle`?";
        var choices = entities.Take(5).Select(item => $"`{item.Name} {item.Area ?? item.Domain} toggle`").ToArray();
        return choices.Length == 0
            ? "Did you mean a configured Home Assistant device? No devices are currently available."
            : $"Did you mean one of: {string.Join(", ", choices)}?";
    }

    private static string NormalizeHomeTerm(string value) => value.ToLowerInvariant() switch
    {
        "lights" or "lamps" => "light",
        "thermostat" or "thermostats" or "temperature" => "climate",
        "fans" => "fan",
        "switches" => "switch",
        _ => value.ToLowerInvariant()
    };

    private async Task<CommandCenterActionResult?> TryPersonalCommandAsync(
        string[] parts,
        string[] tokens,
        string actor,
        CancellationToken cancellationToken)
    {
        if (tokens[0] == "request")
            return await ProcessPersonalAsync($"media add {string.Join(' ', parts.Skip(1))}", actor, cancellationToken);
        if (tokens[0] == "missing")
            return await MediaSummaryAsync(true, cancellationToken);
        if (tokens[0] == "queue")
            return await MediaSummaryAsync(false, cancellationToken);
        if (tokens[0] == "add" && parts.Length >= 3 && IsItemKind(tokens[1]))
            return await ProcessPersonalAsync($"{tokens[1]} add {string.Join(' ', parts.Skip(2))}", actor, cancellationToken);
        if (tokens[0] == "list" && parts.Length >= 2 && IsItemKind(tokens[1]))
            return await ProcessPersonalAsync($"{tokens[1]} list", actor, cancellationToken);
        if (tokens[0] == "done" && parts.Length >= 2)
            return await ProcessPersonalAsync($"task done {string.Join(' ', parts.Skip(1))}", actor, cancellationToken);
        if (tokens[0] == "remove" && parts.Length >= 3 && IsItemKind(tokens[1]))
            return await ProcessPersonalAsync($"{tokens[1]} remove {string.Join(' ', parts.Skip(2))}", actor, cancellationToken);
        return null;
    }

    private async Task<CommandCenterActionResult> ProcessPersonalAsync(string command, string actor, CancellationToken cancellationToken)
    {
        var message = await processor.ProcessAsync(command, actor, cancellationToken);
        return new CommandCenterActionResult(!message.StartsWith("Unknown command.", StringComparison.OrdinalIgnoreCase), message);
    }

    private async Task<CommandCenterActionResult> MediaSummaryAsync(bool missing, CancellationToken cancellationToken)
    {
        var snapshot = await operations.GetSnapshotAsync(cancellationToken);
        if (missing)
        {
            var items = snapshot.Arr.Instances.Where(item => item.MissingCount > 0)
                .Select(item => $"- {item.Name}: {item.MissingCount} missing").ToArray();
            var total = snapshot.Arr.Instances.Sum(item => item.MissingCount);
            return new CommandCenterActionResult(true, items.Length == 0
                ? "Missing media: none reported."
                : $"Missing media: {total} total.\n{string.Join("\n", items)}");
        }

        var queue = snapshot.Arr.Queue.Take(10).Select(item => $"- {item.Title} ({item.Source}, {item.ProgressPercent:0}%)").ToArray();
        var more = Math.Max(0, snapshot.Arr.Queue.Count - queue.Length);
        return new CommandCenterActionResult(true, queue.Length == 0
            ? "Download queue: empty."
            : $"Download queue: {snapshot.Arr.Queue.Count} item{(snapshot.Arr.Queue.Count == 1 ? "" : "s")}.\n{string.Join("\n", queue)}{(more > 0 ? $"\n+{more} more." : "")}");
    }

    private static bool IsItemKind(string value) => value.ToLowerInvariant() is
        "task" or "tasks" or "note" or "notes" or "shopping" or "shop" or "calendar" or "agenda" or
        "package" or "delivery" or "media" or "automation";

    private static string FitDiscordMessage(IEnumerable<string> sections)
    {
        const int limit = 1900;
        var result = new List<string>();
        var length = 0;
        foreach (var section in sections)
        {
            var addition = result.Count == 0 ? section : $"\n\n{section}";
            if (length + addition.Length > limit) break;
            result.Add(section);
            length += addition.Length;
        }

        return string.Join("\n\n", result);
    }

    private async Task<ServiceCard?> ResolveServiceAsync(string query, CancellationToken cancellationToken)
    {
        var value = query.Trim();
        var all = await services.GetServicesAsync(cancellationToken);
        return all.FirstOrDefault(item => item.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(item => item.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(item => item.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static CommandCenterActionResult Fail(string message) => new(false, $"{message}\n{Help()}");

    private static bool RequiresAdministrator(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2 && (parts[0].Equals("machine", StringComparison.OrdinalIgnoreCase)
            || (parts[0].Equals("restart", StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals("service", StringComparison.OrdinalIgnoreCase)));
    }

    private static string Help() => "Supported commands: restart service <name>, backup now, restore <id>, maintenance <task>, machine <lock|sleep|restart|shutdown> <agentId>, list rules, run rule <name>, enable rule <name>, disable rule <name>, status, health.";
}

