using HomeDashboard.Api;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class RestartCoordinatorTests
{
    [Fact]
    public void QueueRestart_rejects_unknown_service()
    {
        var coordinator = new RestartCoordinator(Options.Create(new DashboardOptions()));

        var result = coordinator.QueueRestart("missing", new RestartRequest("test", null));

        Assert.Equal(RestartState.Rejected, result.State);
    }

    [Fact]
    public void QueueRestart_returns_queued_when_enabled()
    {
        var coordinator = new RestartCoordinator(Options.Create(new DashboardOptions
        {
            Services =
            [
                new ServiceDefinition
                {
                    Id = "plex",
                    Name = "Plex",
                    RestartEnabled = true
                }
            ]
        }));

        var result = coordinator.QueueRestart("plex", new RestartRequest("test", null));

        Assert.Equal(RestartState.Queued, result.State);
    }
}
