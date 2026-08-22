namespace HomeDashboard.Contracts;

public sealed record DashboardSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<ServiceCard> Services,
    SystemStats System,
    IReadOnlyList<NewsItem> News,
    IReadOnlyList<AgentSummary> Agents,
    IReadOnlyList<DashboardNotification> Notifications,
    IReadOnlyList<AuditEvent> RecentAuditEvents);

public sealed record AgentSnapshot(
    string AgentId,
    DateTimeOffset CapturedAt,
    SystemStats System,
    IReadOnlyList<ServiceCard> Services);

public sealed record AgentSummary(
    string AgentId,
    string Hostname,
    DateTimeOffset LastSeenAt,
    ServiceStatus Status,
    int ServicesMonitored);

public sealed record AgentHistoryPoint(
    string AgentId,
    DateTimeOffset CapturedAt,
    double CpuPercent,
    double MemoryUsedPercent,
    int ServicesOnline,
    int ServicesDegraded,
    int ServicesOffline);

public sealed record ServiceCard(
    string Id,
    string Name,
    ServiceKind Kind,
    string Description,
    Uri? Url,
    ServiceStatus Status,
    bool RestartEnabled,
    DateTimeOffset? LastCheckedAt,
    string? StatusMessage,
    IReadOnlyList<ServiceMetric> Metrics);

public sealed record ServiceMetric(
    string Label,
    string Value);

public enum ServiceKind
{
    Generic,
    Plex,
    Sonarr,
    Radarr,
    Lidarr,
    Readarr,
    Prowlarr,
    Bazarr,
    qBittorrent,
    SABnzbd,
    Jellyfin,
    GameServer,
    FileShare
}

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
    string? Summary,
    NewsContentKind Kind = NewsContentKind.Article,
    string Category = "Technology",
    Uri? ProviderUrl = null);

public enum NewsContentKind
{
    Article,
    Podcast
}

public sealed record RestartRequest(
    string RequestedBy,
    string? Reason,
    bool Confirmed = false);

public sealed record RestartResult(
    string ServiceId,
    RestartState State,
    string Message,
    DateTimeOffset RequestedAt,
    string? CommandId = null);

public enum RestartState
{
    Queued,
    Rejected,
    Unsupported
}

public sealed record LoginRequest(
    string Password);

public sealed record AuthSession(
    bool IsAuthenticated,
    DateTimeOffset? ExpiresAt);

public sealed record AgentCommand(
    string Id,
    string AgentId,
    AgentCommandKind Kind,
    string ServiceId,
    string RequestedBy,
    string? Reason,
    DateTimeOffset RequestedAt,
    AgentCommandState State,
    string? Message = null,
    DateTimeOffset? CompletedAt = null);

public enum AgentCommandKind
{
    RestartService
}

public enum AgentCommandState
{
    Queued,
    Running,
    Succeeded,
    Failed
}

public sealed record AgentCommandCompletion(
    bool Succeeded,
    string Message);

public sealed record DashboardNotification(
    string Id,
    NotificationSeverity Severity,
    string Title,
    string Message,
    DateTimeOffset CreatedAt);

public enum NotificationSeverity
{
    Info,
    Warning,
    Critical
}

public sealed record AuditEvent(
    string Id,
    AuditEventType Type,
    string Message,
    string? ServiceId,
    string? AgentId,
    string Actor,
    DateTimeOffset OccurredAt,
    string? CommandId = null,
    bool Succeeded = true);

public enum AuditEventType
{
    SetupSaved,
    RestartQueued,
    RestartCompleted,
    RestartRejected,
    AgentSnapshotReceived
}

public sealed record SetupStatus(
    bool IsConfigured,
    bool UsesPlaceholderSecrets,
    bool RequiresRestart,
    string? DefaultAgentId,
    int ServiceCount,
    int NewsFeedCount);

public sealed record SetupRequest(
    string DashboardPassword,
    string? DashboardApiKey,
    string? AgentApiKey,
    string DefaultAgentId,
    IReadOnlyList<ServiceSetupRequest> Services,
    IReadOnlyList<NewsFeedSetupRequest> NewsFeeds);

public sealed record ServiceSetupRequest(
    string Id,
    string Name,
    ServiceKind Kind,
    string Description,
    string? Url,
    string? HealthUrl,
    string? ApiKey,
    bool RestartEnabled);

public sealed record NewsFeedSetupRequest(
    string Name,
    string Url);

public sealed record DashboardSettings(
    string DefaultAgentId,
    bool IncludeRecommendedFeeds,
    IReadOnlyList<ServiceSetting> Services,
    IReadOnlyList<NewsFeedSetting> NewsFeeds,
    bool RequiresRestart = false);

public sealed record ServiceSetting(
    string Id,
    string Name,
    ServiceKind Kind,
    string Description,
    string? Url,
    string? HealthUrl,
    bool HasApiKey,
    bool RestartEnabled);

public sealed record NewsFeedSetting(
    string Name,
    string Url,
    NewsContentKind Kind,
    string Category,
    string? ProviderUrl);

public sealed record UpdateDashboardSettingsRequest(
    string DefaultAgentId,
    bool IncludeRecommendedFeeds,
    IReadOnlyList<UpdateServiceSetting> Services,
    IReadOnlyList<NewsFeedSetting> NewsFeeds);

public sealed record UpdateServiceSetting(
    string Id,
    string Name,
    ServiceKind Kind,
    string Description,
    string? Url,
    string? HealthUrl,
    string? ApiKey,
    bool ClearApiKey,
    bool RestartEnabled);
