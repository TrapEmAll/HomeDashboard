using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public sealed class Worker(
    IAgentCollector collector,
    IAgentPublisher publisher,
    IAgentCommandExecutor commandExecutor,
    IOptions<AgentOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = collector.Collect();
            try
            {
                await publisher.PublishAsync(snapshot, stoppingToken);
                logger.LogInformation(
                    "Published {ServiceCount} service states for {Host} at {CapturedAt}",
                    snapshot.Services.Count,
                    snapshot.System.Hostname,
                    snapshot.CapturedAt);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Failed to publish agent snapshot to dashboard API.");
            }

            try
            {
                var command = await publisher.GetNextCommandAsync(stoppingToken);
                if (command is not null)
                {
                    logger.LogInformation("Executing command {CommandId} for service {ServiceId}.", command.Id, command.ServiceId);
                    var completion = commandExecutor.Execute(command);
                    await publisher.CompleteCommandAsync(command, completion, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Failed to process agent command.");
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
