namespace HomeDashboard.Contracts;

public sealed record CommandCenterSnapshot(
    DateTimeOffset GeneratedAt,
    string ActiveMode,
    DailyBriefing Briefing,
    IReadOnlyList<PersonalTask> Tasks,
    IReadOnlyList<CalendarEntry> Calendar,
    IReadOnlyList<QuickNote> Notes,
    IReadOnlyList<ShoppingItem> Shopping,
    IReadOnlyList<TrackedPackage> Packages,
    IReadOnlyList<MediaRequestItem> MediaRequests,
    IReadOnlyList<CommandCenterNotification> Inbox,
    IReadOnlyList<IntegrationStatus> Integrations,
    IReadOnlyList<HomeEntity> HomeEntities,
    IReadOnlyList<OperationalAsset> Assets,
    IReadOnlyList<AutomationRule> Automations,
    IReadOnlyList<HouseholdProfile> Profiles,
    IReadOnlyList<CommandCenterActivity> Activity);

public sealed record CommandCenterArchive(
    string ActiveMode,
    IReadOnlyList<PersonalTask> Tasks,
    IReadOnlyList<CalendarEntry> Calendar,
    IReadOnlyList<QuickNote> Notes,
    IReadOnlyList<ShoppingItem> Shopping,
    IReadOnlyList<TrackedPackage> Packages,
    IReadOnlyList<MediaRequestItem> MediaRequests,
    IReadOnlyList<CommandCenterNotification> Inbox,
    IReadOnlyList<IntegrationArchive> Integrations,
    IReadOnlyList<HomeEntity> HomeEntities,
    IReadOnlyList<OperationalAsset> Assets,
    IReadOnlyList<AutomationRule> Automations,
    IReadOnlyList<HouseholdProfile> Profiles,
    IReadOnlyList<CommandCenterActivity> Activity);

public sealed record IntegrationArchive(
    string Id,
    string Kind,
    string Name,
    bool Enabled,
    string? BaseUrl,
    IReadOnlyDictionary<string, string> Settings);

public sealed record CommandCenterActivity(
    string Id,
    string Tool,
    string? Target,
    string Message,
    DateTimeOffset OccurredAt,
    bool Succeeded);

public sealed record DailyBriefing(
    string Greeting,
    string Summary,
    IReadOnlyList<string> Highlights,
    int AttentionCount,
    DateTimeOffset GeneratedAt);

public sealed record PersonalTask(
    string Id,
    string Title,
    string? Details,
    string List,
    ItemPriority Priority,
    DateTimeOffset? DueAt,
    bool Completed,
    DateTimeOffset CreatedAt);

public sealed record CalendarEntry(
    string Id,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string Calendar,
    string? Location,
    string? Url,
    bool AllDay = false);

public sealed record QuickNote(
    string Id,
    string Title,
    string Body,
    IReadOnlyList<string> Tags,
    bool Pinned,
    DateTimeOffset UpdatedAt);

public sealed record ShoppingItem(
    string Id,
    string Name,
    string List,
    int Quantity,
    bool Completed,
    DateTimeOffset CreatedAt);

public sealed record TrackedPackage(
    string Id,
    string Carrier,
    string TrackingNumber,
    string Description,
    string Status,
    DateTimeOffset? EstimatedDelivery,
    DateTimeOffset UpdatedAt);

public sealed record MediaRequestItem(
    string Id,
    string Title,
    string MediaType,
    string Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    Uri? ArtworkUrl = null,
    string? ImdbId = null,
    int? TmdbId = null,
    int? TvdbId = null,
    string? Source = null);

public sealed record CommandCenterNotification(
    string Id,
    NotificationSeverity Severity,
    string Source,
    string Title,
    string Message,
    DateTimeOffset CreatedAt,
    bool Acknowledged = false,
    DateTimeOffset? SnoozedUntil = null,
    IReadOnlyList<NotificationAction>? Actions = null);

public sealed record NotificationAction(
    string Label,
    string Tool,
    string? Target,
    bool RequiresConfirmation);

public sealed record IntegrationStatus(
    string Id,
    string Kind,
    string Name,
    bool Enabled,
    bool Connected,
    string Status,
    DateTimeOffset? LastCheckedAt,
    IReadOnlyList<string> Capabilities,
    string? BaseUrl,
    bool HasSecret,
    IReadOnlyDictionary<string, string> Settings);

public sealed record UpdateIntegrationRequest(
    string Name,
    string? BaseUrl,
    bool Enabled,
    string? Secret,
    IReadOnlyDictionary<string, string>? Settings = null,
    bool ClearSecret = false);

public sealed record HomeEntity(
    string Id,
    string Name,
    string Domain,
    string State,
    string? Area,
    IReadOnlyDictionary<string, string> Attributes,
    DateTimeOffset UpdatedAt);

public sealed record OperationalAsset(
    string Id,
    string Category,
    string Name,
    string Status,
    string? Detail,
    IReadOnlyDictionary<string, string> Metrics,
    DateTimeOffset UpdatedAt,
    string? Url = null);

public sealed record AutomationRule(
    string Id,
    string Name,
    string Trigger,
    string? Condition,
    string ActionTool,
    string? ActionTarget,
    bool Enabled,
    DateTimeOffset? LastRunAt,
    string? LastResult);

public sealed record HouseholdProfile(
    string Id,
    string DisplayName,
    string Role,
    string? Color,
    bool Active);

public enum ItemPriority
{
    Low,
    Normal,
    High,
    Urgent
}

public sealed record CommandCenterItemRequest(
    string Kind,
    string? Id,
    string Title,
    string? Details = null,
    string? Category = null,
    DateTimeOffset? Date = null,
    IReadOnlyDictionary<string, string>? Fields = null);

public sealed record CommandCenterActionRequest(
    string Tool,
    string? Target,
    bool Confirmed = false,
    IReadOnlyDictionary<string, string>? Arguments = null);

public sealed record CommandCenterActionResult(
    bool Succeeded,
    string Message,
    bool RequiresConfirmation = false,
    string? AuditId = null);

public sealed record DiscordCommandRequest(string Command, string Actor, ulong? DiscordUserId = null);

public sealed record CommandCenterBatchRequest(
    IReadOnlyList<CommandCenterActionRequest>? Actions = null,
    IReadOnlyList<CommandCenterDeleteRequest>? Deletes = null);

public sealed record CommandCenterDeleteRequest(string Kind, string Id);

public sealed record AssistantRequest(string Message, bool AllowActions = false);

public sealed record AssistantResponse(
    string Message,
    IReadOnlyList<AssistantSuggestion> Suggestions,
    IReadOnlyList<CommandCenterActionRequest> ProposedActions,
    DateTimeOffset GeneratedAt);

public sealed record AssistantSuggestion(string Label, string Prompt);

public sealed record CommandCenterSearchResult(
    string Id,
    string Kind,
    string Title,
    string? Subtitle,
    string? Action,
    double Score);

public sealed record CommandCenterWebhook(
    string Source,
    string Event,
    string? Title,
    string? Message,
    NotificationSeverity Severity = NotificationSeverity.Info,
    IReadOnlyDictionary<string, string>? Data = null);

public sealed record FileWorkspaceEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset UpdatedAt);

public sealed record SystemLogEntry(
    DateTimeOffset OccurredAt,
    string Level,
    string Source,
    string Message);

