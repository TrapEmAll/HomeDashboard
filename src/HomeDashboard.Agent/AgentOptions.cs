namespace HomeDashboard.Agent;

public sealed class AgentOptions
{
    public Uri DashboardApiUrl { get; init; } = new("http://localhost:5000");
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public IReadOnlyList<WindowsServiceMonitor> WindowsServices { get; init; } = [];
}

public sealed class WindowsServiceMonitor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string ServiceName { get; init; }
    public bool RestartEnabled { get; init; }
}
