using HomeDashboard.Contracts;

namespace HomeDashboard.Api;

public sealed class DashboardOptions
{
    public IReadOnlyList<ServiceDefinition> Services { get; init; } = [];
    public IReadOnlyList<NewsFeedDefinition> NewsFeeds { get; init; } = [];
    public bool IncludeRecommendedFeeds { get; init; } = true;
    public string DefaultAgentId { get; init; } = "server-pc";
    public string DataPath { get; init; } = "data/homedashboard-state.json";
    public int AgentHistoryLimit { get; init; } = 120;
    public string? NetworkProbeTarget { get; init; }
    public int NetworkProbeIntervalSeconds { get; init; } = 30;
    public int HostDetailRefreshSeconds { get; init; } = 60;
    public int DiscordConfirmationTimeoutSeconds { get; init; } = 60;
    public string DiscordUnmatchedLogPath { get; init; } = "data/discord-unmatched.log";
    public int DiscordRateLimitCommands { get; init; } = 10;
    public int DiscordRateLimitWindowSeconds { get; init; } = 60;
}

public sealed class ServiceDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ServiceKind Kind { get; init; } = ServiceKind.Generic;
    public string Description { get; init; } = "";
    public Uri? Url { get; init; }
    public Uri? HealthUrl { get; init; }
    public string? ApiKey { get; init; }
    public bool RestartEnabled { get; init; }
}

public sealed class NewsFeedDefinition
{
    public required string Name { get; init; }
    public required Uri Url { get; init; }
    public NewsContentKind Kind { get; init; } = NewsContentKind.Article;
    public string Category { get; init; } = "Technology";
    public Uri? ProviderUrl { get; init; }
}

