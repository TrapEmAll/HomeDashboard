using HomeDashboard.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class SetupServiceTests
{
    [Fact]
    public void Setup_status_is_configured_when_password_hash_is_saved()
    {
        var setup = new SetupService(
            Options.Create(new DashboardOptions
            {
                DefaultAgentId = "server-pc",
                DataPath = "dashboard-state.test.json"
            }),
            Options.Create(new DashboardSecurityOptions
            {
                DashboardApiKey = "dashboard-secret",
                AgentApiKey = "agent-secret",
                DashboardPassword = "",
                DashboardPasswordHash = ApiKeyValidator.HashSecret("dashboard-password")
            }),
            new InMemoryAgentCommandStore());

        var status = setup.GetStatus();

        Assert.True(status.IsConfigured);
        Assert.False(status.UsesPlaceholderSecrets);
    }

    private sealed class InMemoryAgentCommandStore : IAgentCommandStore
    {
        public AgentCommand Enqueue(string agentId, string serviceId, RestartRequest request)
            => throw new NotSupportedException();

        public AgentCommand? DequeueNext(string agentId)
            => throw new NotSupportedException();

        public void Complete(string agentId, string commandId, AgentCommandCompletion completion)
            => throw new NotSupportedException();

        public IReadOnlyList<AgentCommand> GetRecentCommands(int count)
            => [];

        public IReadOnlyList<AuditEvent> GetRecentAuditEvents(int count)
            => [];

        public void AddAuditEvent(AuditEvent auditEvent)
        {
        }
    }
}
