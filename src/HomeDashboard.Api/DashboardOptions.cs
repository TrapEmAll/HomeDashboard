namespace HomeDashboard.Api;

public sealed class DashboardOptions
{
    public IReadOnlyList<ServiceDefinition> Services { get; init; } = [];
    public IReadOnlyList<NewsFeedDefinition> NewsFeeds { get; init; } = [];
    public string DefaultAgentId { get; init; } = "server-pc";
}

public sealed class ServiceDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public Uri? Url { get; init; }
    public Uri? HealthUrl { get; init; }
    public bool RestartEnabled { get; init; }
}

public sealed class NewsFeedDefinition
{
    public required string Name { get; init; }
    public required Uri Url { get; init; }
}
