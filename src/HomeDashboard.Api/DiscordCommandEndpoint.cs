using HomeDashboard.Contracts;

namespace HomeDashboard.Api;

internal static class DiscordCommandEndpoint
{
    public static async Task<CommandCenterActionResult> HandleAsync(
        DiscordCommandRequest request,
        DiscordCommandProcessor processor,
        IAgentCommandStore auditStore,
        CancellationToken cancellationToken)
    {
        var command = (request.Command ?? "").Trim();
        var actor = string.IsNullOrWhiteSpace(request.Actor) ? "discord" : request.Actor.Trim();
        var auditId = Guid.NewGuid().ToString("n");
        var receivedAt = DateTimeOffset.UtcNow;

        auditStore.AddAuditEvent(new AuditEvent(
            auditId,
            AuditEventType.DiscordCommandReceived,
            $"Discord command received from {actor}: {Short(command, 120)}",
            null,
            null,
            actor,
            receivedAt,
            auditId));

        try
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                var emptyResult = new CommandCenterActionResult(false, "Missing command details.", AuditId: auditId);
                RecordCompletion(auditStore, auditId, actor, emptyResult);
                return emptyResult;
            }

            var message = await processor.ProcessAsync(command, actor, cancellationToken);
            var result = new CommandCenterActionResult(true, message, AuditId: auditId);
            RecordCompletion(auditStore, auditId, actor, result);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var result = new CommandCenterActionResult(false, ex.Message, AuditId: auditId);
            RecordCompletion(auditStore, auditId, actor, result);
            return result;
        }
    }

    private static void RecordCompletion(IAgentCommandStore auditStore, string auditId, string actor, CommandCenterActionResult result)
        => auditStore.AddAuditEvent(new AuditEvent(
            Guid.NewGuid().ToString("n"),
            AuditEventType.DiscordCommandCompleted,
            result.Message,
            null,
            null,
            actor,
            DateTimeOffset.UtcNow,
            auditId,
            result.Succeeded));

    private static string Short(string value, int length)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(empty)";
        return value.Length <= length ? value : value[..(length - 3)] + "...";
    }
}
