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

    private static string Help() => "Supported commands: restart service <name>, backup now, restore <id>, maintenance <task>, machine <lock|sleep|restart|shutdown> <agentId>, status, health.";
}

