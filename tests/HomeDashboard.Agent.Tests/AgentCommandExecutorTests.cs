using HomeDashboard.Agent;
using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Agent.Tests;

public sealed class AgentCommandExecutorTests
{
    [Fact]
    public void MachineActionsAreDisabledByDefault()
    {
        var executor = new AgentCommandExecutor(new StaticOptionsMonitor<AgentOptions>(new AgentOptions()));

        var result = executor.Execute(new AgentCommand("command", "agent", AgentCommandKind.ShutdownComputer, "machine",
            "dashboard", null, DateTimeOffset.UtcNow, AgentCommandState.Queued));

        Assert.False(result.Succeeded);
        Assert.Contains("disabled", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
