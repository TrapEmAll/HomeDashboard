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
    DateTimeOffset CapturedAt,
    long UptimeSeconds = 0,
    string? OsVersion = null,
    bool PendingReboot = false,
    long NetworkReceiveBytesPerSecond = 0,
    long NetworkSendBytesPerSecond = 0,
    IReadOnlyList<ProcessStats>? TopProcesses = null);

public sealed record ProcessStats(
    int ProcessId,
    string Name,
    long WorkingSetBytes,
    TimeSpan CpuTime);

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
    Uri? ProviderUrl = null,
    Uri? MediaUrl = null,
    Uri? ImageUrl = null,
    string? Duration = null);

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
    string Password,
    string? Username = null);

public sealed record AuthSession(
    bool IsAuthenticated,
    DateTimeOffset? ExpiresAt,
    string? ProfileId = null,
    string? DisplayName = null,
    string? Role = null);

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
    RestartService,
    LockComputer,
    SleepComputer,
    RestartComputer,
    ShutdownComputer
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

public sealed record OpmlImportRequest(
    string Content);

public sealed record OpmlImportPreview(
    IReadOnlyList<NewsFeedSetting> Feeds,
    int FeedOutlineCount,
    int SkippedCount);

public sealed record OperationsSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<OperationsActivity> Activity,
    IReadOnlyList<MediaCalendarItem> Calendar,
    IReadOnlyList<PlaybackSession> PlaybackSessions,
    IReadOnlyList<DownloadQueueItem> Downloads,
    IReadOnlyList<ServiceUptimeSummary> Uptime,
    IReadOnlyList<StorageForecast> Storage,
    IReadOnlyList<IncidentSummary> Incidents,
    IReadOnlyList<MaintenanceWindow> Maintenance,
    UpdateSummary Update);

public sealed record OperationsActivity(
    string Id,
    DateTimeOffset OccurredAt,
    string Source,
    string Title,
    string Detail,
    OperationsActivityKind Kind,
    NotificationSeverity Severity = NotificationSeverity.Info);

public enum OperationsActivityKind
{
    Service,
    Media,
    Download,
    Playback,
    Maintenance,
    Security,
    System
}

public sealed record MediaCalendarItem(
    string Id,
    string Source,
    string Title,
    string? Subtitle,
    DateTimeOffset AirsAt,
    string MediaType,
    bool Monitored,
    bool HasFile,
    Uri? Url = null);

public sealed record PlaybackSession(
    string Id,
    string User,
    string Title,
    string? Subtitle,
    string Player,
    string Decision,
    int ProgressPercent,
    string? VideoResolution,
    long? BandwidthKbps);

public sealed record DownloadQueueItem(
    string Id,
    string Source,
    string Name,
    string Status,
    double ProgressPercent,
    long? SizeBytes,
    long? RemainingBytes,
    long? DownloadSpeedBytes,
    TimeSpan? Eta,
    bool CanPause,
    bool CanRemove);

public sealed record DownloadControlRequest(
    string Source,
    string ItemId,
    DownloadControlAction Action,
    bool DeleteData = false);

public enum DownloadControlAction
{
    Pause,
    Resume,
    Recheck,
    Remove
}

public sealed record ServiceUptimeSummary(
    string ServiceId,
    string Name,
    double UptimePercent,
    DateTimeOffset WindowStartedAt,
    int IncidentCount,
    ServiceStatus CurrentStatus);

public sealed record StorageForecast(
    string Name,
    long TotalBytes,
    long FreeBytes,
    double UsedPercent,
    long? DailyGrowthBytes,
    int? DaysRemaining);

public sealed record IncidentSummary(
    string Id,
    string ServiceId,
    string ServiceName,
    NotificationSeverity Severity,
    string Message,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt = null);

public sealed record MaintenanceWindow(
    string Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? ServiceId,
    bool SuppressAlerts,
    string CreatedBy);

public sealed record CreateMaintenanceWindowRequest(
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string? ServiceId,
    bool SuppressAlerts);

public sealed record UpdateSummary(
    string CurrentVersion,
    string Channel,
    Uri RepositoryUrl,
    DateTimeOffset? LastCheckedAt,
    bool UpdateAvailable,
    string? LatestVersion);

public sealed record ServiceDiscoveryResult(
    IReadOnlyList<DiscoveredService> Services,
    DateTimeOffset ScannedAt);

public sealed record DiscoveredService(
    string Id,
    string Name,
    ServiceKind Kind,
    Uri Url,
    int Port,
    bool AlreadyConfigured);

public sealed record DashboardBackup(
    int FormatVersion,
    DateTimeOffset CreatedAt,
    DashboardSettings Settings,
    IReadOnlyList<MaintenanceWindow> Maintenance,
    string ApplicationVersion,
    CommandCenterArchive? CommandCenter = null);
