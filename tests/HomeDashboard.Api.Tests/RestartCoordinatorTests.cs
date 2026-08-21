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
        var coordinator = new RestartCoordinator(Options.Create(new DashboardOptions()), CreateStore());

        var result = coordinator.QueueRestart("missing", new RestartRequest("test", null));

        Assert.Equal(RestartState.Rejected, result.State);
    }

    [Fact]
    public void QueueRestart_returns_queued_when_enabled()
    {
        var store = CreateStore();
        var coordinator = new RestartCoordinator(Options.Create(new DashboardOptions
        {
            DefaultAgentId = "server-pc",
            Services =
            [
                new ServiceDefinition
                {
                    Id = "plex",
                    Name = "Plex",
                    RestartEnabled = true
                }
            ]
        }), store);

        var result = coordinator.QueueRestart("plex", new RestartRequest("test", null, true));

        Assert.Equal(RestartState.Queued, result.State);
        Assert.NotNull(result.CommandId);
        Assert.Equal(result.CommandId, store.DequeueNext("server-pc")?.Id);
    }

    [Fact]
    public void QueueRestart_rejects_unconfirmed_restart()
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
        }), CreateStore());

        var result = coordinator.QueueRestart("plex", new RestartRequest("test", null));

        Assert.Equal(RestartState.Rejected, result.State);
    }

    private static FileDashboardStateStore CreateStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "homedashboard-tests", $"{Guid.NewGuid():n}.json");
        return new FileDashboardStateStore(Options.Create(new DashboardOptions { DataPath = path }));
    }
}
