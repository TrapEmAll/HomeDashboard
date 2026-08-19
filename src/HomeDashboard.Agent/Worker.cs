using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public sealed class Worker(
    IAgentCollector collector,
    IAgentPublisher publisher,
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

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
