namespace HomeDashboard.Contracts;

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ServiceCard> Services,
    SystemStats System,
    IReadOnlyList<NewsItem> News);

public sealed record AgentSnapshot(
    string AgentId,
    DateTimeOffset CapturedAt,
    SystemStats System,
    IReadOnlyList<ServiceCard> Services);

public sealed record ServiceCard(
    string Id,
    string Name,
    string Description,
    Uri? Url,
    ServiceStatus Status,
    bool RestartEnabled,
    DateTimeOffset? LastCheckedAt,
    string? StatusMessage);

public enum ServiceStatus
{
    Unknown,
    Online,
    Degraded,
    Offline
}

public sealed record SystemStats(
    string Hostname,
    double CpuPercent,
    double MemoryUsedPercent,
    IReadOnlyList<DiskStats> Disks,
    DateTimeOffset CapturedAt);

public sealed record DiskStats(
    string Name,
    long TotalBytes,
    long FreeBytes);

public sealed record NewsItem(
    string Source,
    string Title,
    Uri? Url,
    DateTimeOffset? PublishedAt,
    string? Summary);

public sealed record RestartRequest(
    string RequestedBy,
    string? Reason);

public sealed record RestartResult(
    string ServiceId,
    RestartState State,
    string Message,
    DateTimeOffset RequestedAt);

public enum RestartState
{
    Queued,
    Rejected,
    Unsupported
}
