using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HomeDashboard.Agent;

public sealed class Worker(
    IAgentCollector collector,
    IOptions<AgentOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var snapshot = collector.Collect();
            logger.LogInformation(
                "Collected {ServiceCount} service states for {Host} at {CapturedAt}",
                snapshot.Services.Count,
                snapshot.System.Hostname,
                snapshot.CapturedAt);

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
