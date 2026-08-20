using HomeDashboard.Api;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeDashboard.Api.Tests;

public sealed class SecurityTests
{
    [Fact]
    public void Dashboard_key_validator_accepts_matching_key()
    {
        var validator = new ApiKeyValidator(Options.Create(new DashboardSecurityOptions
        {
            DashboardApiKey = "dashboard-secret",
            AgentApiKey = "agent-secret",
            DashboardPassword = "dashboard-password"
        }));

        Assert.True(validator.IsDashboardKeyValid("dashboard-secret"));
        Assert.False(validator.IsDashboardKeyValid("agent-secret"));
        Assert.True(validator.IsDashboardPasswordValid("dashboard-password"));
    }

    [Fact]
    public void Agent_key_validator_rejects_missing_or_wrong_key()
    {
        var validator = new ApiKeyValidator(Options.Create(new DashboardSecurityOptions
        {
            DashboardApiKey = "dashboard-secret",
            AgentApiKey = "agent-secret"
        }));

        Assert.False(validator.IsAgentKeyValid(null));
        Assert.False(validator.IsAgentKeyValid("dashboard-secret"));
        Assert.True(validator.IsAgentKeyValid("agent-secret"));
    }
}
