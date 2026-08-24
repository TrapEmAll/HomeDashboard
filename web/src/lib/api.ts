import type { AgentHistoryPoint, ArrCommandAction, ArrCommandResult, AuditEvent, DashboardNotification, DashboardSettings, DashboardSnapshot, DownloadControlAction, MaintenanceWindow, NewsContentKind, NewsItem, OperationsSnapshot, OpmlImportPreview, ServiceCard, ServiceDiscoveryResult, ServiceKind, ServiceMetric, ServiceStatus, SetupRequest, SetupStatus, SystemStats, UpdateDashboardSettingsRequest } from "../types/dashboard";
import type { AssistantResponse, CommandCenterActionRequest, CommandCenterActionResult, CommandCenterItemRequest, CommandCenterSnapshot, FileWorkspaceEntry, IntegrationStatus, SearchResult, SystemLogEntry } from "../types/commandCenter";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "";
let dashboardRequest: Promise<DashboardSnapshot> | null = null;
let operationsRequest: Promise<OperationsSnapshot> | null = null;
let commandCenterRequest: Promise<CommandCenterSnapshot> | null = null;

export class ApiError extends Error {
  public constructor(public readonly status: number, message: string) {
    super(message);
    this.name = "ApiError";
  }
}

export interface AuthSession {
  isAuthenticated: boolean;
  expiresAt?: string | null;
  profileId?: string | null;
  displayName?: string | null;
  role?: string | null;
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { error?: string } | null;
    throw new ApiError(response.status, body?.error ?? `Request failed with ${response.status}`);
  }

  return response.json() as Promise<T>;
}

export async function getSession(): Promise<AuthSession> {
  const response = await fetch(`${apiBaseUrl}/auth/session`, { credentials: "include" });
  return readJson<AuthSession>(response);
}

export async function login(password: string, username?: string): Promise<AuthSession> {
  const response = await fetch(`${apiBaseUrl}/auth/login`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password, username: username || null })
  });

  return readJson<AuthSession>(response);
}

export async function logout(): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/auth/logout`, {
    method: "POST",
    credentials: "include"
  });
  if (!response.ok && response.status !== 204) {
    throw new Error(`Logout failed with ${response.status}`);
  }
}

export async function getSetupStatus(): Promise<SetupStatus> {
  const response = await fetch(`${apiBaseUrl}/setup/status`, { credentials: "include" });
  return readJson<SetupStatus>(response);
}

export async function saveSetup(request: SetupRequest): Promise<SetupStatus> {
  const response = await fetch(`${apiBaseUrl}/setup`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });

  return readJson<SetupStatus>(response);
}

export function getDashboard(): Promise<DashboardSnapshot> {
  if (dashboardRequest) return dashboardRequest;
  dashboardRequest = fetch(`${apiBaseUrl}/api/dashboard`, { credentials: "include" })
    .then(async (response) => {
      if (!response.ok) throw new ApiError(response.status, `Dashboard request failed with ${response.status}`);
      return normalizeDashboard(await response.json());
    })
    .finally(() => { dashboardRequest = null; });
  return dashboardRequest;
}

export async function getDashboardSettings(): Promise<DashboardSettings> {
  const response = await fetch(`${apiBaseUrl}/api/settings`, { credentials: "include" });
  return normalizeSettings(await readJson<unknown>(response));
}

export async function updateDashboardSettings(request: UpdateDashboardSettingsRequest): Promise<DashboardSettings> {
  const response = await fetch(`${apiBaseUrl}/api/settings`, {
    method: "PUT",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
  return normalizeSettings(await readJson<unknown>(response));
}

export async function importOpmlFeeds(content: string): Promise<OpmlImportPreview> {
  const response = await fetch(`${apiBaseUrl}/api/settings/import-opml`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ content })
  });
  const raw = asRecord(await readJson<unknown>(response));
  return {
    feeds: asArray(read(raw, "feeds", "Feeds"), (feed) => {
      const item = asRecord(feed);
      return {
        name: asString(read(item, "name", "Name"), "Imported feed"),
        url: asString(read(item, "url", "Url"), ""),
        kind: normalizeNewsKind(read(item, "kind", "Kind")),
        category: asString(read(item, "category", "Category"), "Imported"),
        providerUrl: asOptionalString(read(item, "providerUrl", "ProviderUrl"))
      };
    }),
    feedOutlineCount: asNumber(read(raw, "feedOutlineCount", "FeedOutlineCount"), 0),
    skippedCount: asNumber(read(raw, "skippedCount", "SkippedCount"), 0)
  };
}

export async function getAgentHistory(agentId: string): Promise<AgentHistoryPoint[]> {
  const response = await fetch(`${apiBaseUrl}/api/agent/${encodeURIComponent(agentId)}/history`, { credentials: "include" });
  return asArray(await readJson<unknown>(response), normalizeHistoryPoint);
}

export async function requestRestart(serviceId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/services/${serviceId}/restart`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ requestedBy: "dashboard", reason: "Manual dashboard action", confirmed: true })
  });

  if (!response.ok && response.status !== 202) {
    throw new Error(`Restart request failed with ${response.status}`);
  }
}

export function getOperations(): Promise<OperationsSnapshot> {
  if (operationsRequest) return operationsRequest;
  operationsRequest = fetch(`${apiBaseUrl}/api/operations`, { credentials: "include" })
    .then((response) => readJson<OperationsSnapshot>(response))
    .finally(() => { operationsRequest = null; });
  return operationsRequest;
}

export async function runArrCommand(serviceId: string, action: ArrCommandAction, confirmed = false): Promise<ArrCommandResult> {
  const response = await fetch(`${apiBaseUrl}/api/operations/arr/command`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ serviceId, action, confirmed })
  });
  if (response.status === 409) return response.json() as Promise<ArrCommandResult>;
  return readJson<ArrCommandResult>(response);
}

export function getCommandCenter(): Promise<CommandCenterSnapshot> {
  if (commandCenterRequest) return commandCenterRequest;
  commandCenterRequest = fetch(`${apiBaseUrl}/api/command-center`, { credentials: "include" })
    .then((response) => readJson<CommandCenterSnapshot>(response))
    .finally(() => { commandCenterRequest = null; });
  return commandCenterRequest;
}

export async function saveCommandCenterItem(request: CommandCenterItemRequest): Promise<CommandCenterSnapshot> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/items`, {
    method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
  });
  return readJson<CommandCenterSnapshot>(response);
}

export async function deleteCommandCenterItem(kind: string, id: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/items/${encodeURIComponent(kind)}/${encodeURIComponent(id)}`, { method: "DELETE", credentials: "include" });
  if (!response.ok && response.status !== 204) throw new ApiError(response.status, `Delete failed with ${response.status}`);
}

export async function runCommandCenterAction(request: CommandCenterActionRequest): Promise<CommandCenterActionResult> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/actions`, {
    method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
  });
  if (response.status === 409) return response.json() as Promise<CommandCenterActionResult>;
  return readJson<CommandCenterActionResult>(response);
}

export async function searchCommandCenter(query: string): Promise<SearchResult[]> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/search?q=${encodeURIComponent(query)}`, { credentials: "include" });
  return readJson<SearchResult[]>(response);
}

export async function askAssistant(message: string): Promise<AssistantResponse> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/assistant`, {
    method: "POST", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ message, allowActions: false })
  });
  return readJson<AssistantResponse>(response);
}

export async function updateCommandCenterIntegration(id: string, request: { name: string; baseUrl?: string | null; enabled: boolean; secret?: string | null; settings?: Record<string, string>; clearSecret?: boolean }): Promise<IntegrationStatus> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/integrations/${encodeURIComponent(id)}`, {
    method: "PUT", credentials: "include", headers: { "Content-Type": "application/json" }, body: JSON.stringify(request)
  });
  return readJson<IntegrationStatus>(response);
}

export async function browseCommandCenterFiles(path?: string): Promise<FileWorkspaceEntry[]> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/files${path ? `?path=${encodeURIComponent(path)}` : ""}`, { credentials: "include" });
  return readJson<FileWorkspaceEntry[]>(response);
}

export async function getCommandCenterLogs(): Promise<SystemLogEntry[]> {
  const response = await fetch(`${apiBaseUrl}/api/command-center/logs?count=100`, { credentials: "include" });
  return readJson<SystemLogEntry[]>(response);
}

export async function controlDownload(source: string, itemId: string, action: DownloadControlAction, deleteData = false): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/downloads/control`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ source, itemId, action, deleteData })
  });
  await readJson<unknown>(response);
}

export async function discoverServices(): Promise<ServiceDiscoveryResult> {
  const response = await fetch(`${apiBaseUrl}/api/discovery`, { credentials: "include" });
  return readJson<ServiceDiscoveryResult>(response);
}

export async function createMaintenanceWindow(request: Omit<MaintenanceWindow, "id" | "createdBy">): Promise<MaintenanceWindow> {
  const response = await fetch(`${apiBaseUrl}/api/maintenance`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });
  return readJson<MaintenanceWindow>(response);
}

export async function removeMaintenanceWindow(id: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/maintenance/${encodeURIComponent(id)}`, { method: "DELETE", credentials: "include" });
  if (!response.ok && response.status !== 204) {
    throw new ApiError(response.status, `Maintenance removal failed with ${response.status}`);
  }
}

export async function downloadBackup(): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/backup`, { credentials: "include" });
  const backup = await readJson<unknown>(response);
  const link = document.createElement("a");
  link.href = URL.createObjectURL(new Blob([JSON.stringify(backup, null, 2)], { type: "application/json" }));
  link.download = `HomeDashboard-backup-${new Date().toISOString().slice(0, 10)}.json`;
  link.click();
  URL.revokeObjectURL(link.href);
}

export async function restoreBackup(content: string): Promise<void> {
  const backup = JSON.parse(content) as unknown;
  const response = await fetch(`${apiBaseUrl}/api/backup/restore`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(backup)
  });
  await readJson<unknown>(response);
}

export function dashboardEventsUrl(): string {
  return `${apiBaseUrl}/api/events`;
}

export function parseDashboardSnapshot(value: string): DashboardSnapshot {
  return normalizeDashboard(JSON.parse(value));
}

function normalizeDashboard(raw: unknown): DashboardSnapshot {
  const value = asRecord(raw);
  return {
    generatedAt: asString(read(value, "generatedAt", "GeneratedAt"), new Date().toISOString()),
    services: asArray<ServiceCard>(read(value, "services", "Services"), normalizeService),
    system: normalizeSystem(read(value, "system", "System")),
    news: asArray<NewsItem>(read(value, "news", "News"), normalizeNewsItem),
    agents: asArray(read(value, "agents", "Agents"), (agent) => {
      const item = asRecord(agent);
      return {
        agentId: asString(read(item, "agentId", "AgentId"), "agent"),
        hostname: asString(read(item, "hostname", "Hostname"), "Unknown host"),
        lastSeenAt: asString(read(item, "lastSeenAt", "LastSeenAt"), new Date().toISOString()),
        status: normalizeStatus(read(item, "status", "Status")),
        servicesMonitored: asNumber(read(item, "servicesMonitored", "ServicesMonitored"), 0)
      };
    }),
    notifications: asArray<DashboardNotification>(read(value, "notifications", "Notifications"), normalizeNotification),
    recentAuditEvents: asArray<AuditEvent>(read(value, "recentAuditEvents", "RecentAuditEvents"), normalizeAuditEvent)
  };
}

function normalizeSettings(raw: unknown): DashboardSettings {
  const value = asRecord(raw);
  return {
    defaultAgentId: asString(read(value, "defaultAgentId", "DefaultAgentId"), "server-pc"),
    includeRecommendedFeeds: read(value, "includeRecommendedFeeds", "IncludeRecommendedFeeds") !== false,
    services: asArray(read(value, "services", "Services"), (service) => {
      const item = asRecord(service);
      return {
        id: asString(read(item, "id", "Id"), "service"),
        name: asString(read(item, "name", "Name"), "Service"),
        kind: normalizeKind(read(item, "kind", "Kind")),
        description: asString(read(item, "description", "Description"), ""),
        url: asOptionalString(read(item, "url", "Url")),
        healthUrl: asOptionalString(read(item, "healthUrl", "HealthUrl")),
        hasApiKey: Boolean(read(item, "hasApiKey", "HasApiKey")),
        restartEnabled: Boolean(read(item, "restartEnabled", "RestartEnabled"))
      };
    }),
    newsFeeds: asArray(read(value, "newsFeeds", "NewsFeeds"), (feed) => {
      const item = asRecord(feed);
      return {
        name: asString(read(item, "name", "Name"), "Feed"),
        url: asString(read(item, "url", "Url"), ""),
        kind: normalizeNewsKind(read(item, "kind", "Kind")),
        category: asString(read(item, "category", "Category"), "Technology"),
        providerUrl: asOptionalString(read(item, "providerUrl", "ProviderUrl"))
      };
    }),
    requiresRestart: Boolean(read(value, "requiresRestart", "RequiresRestart"))
  };
}

function normalizeHistoryPoint(raw: unknown): AgentHistoryPoint {
  const value = asRecord(raw);
  return {
    agentId: asString(read(value, "agentId", "AgentId"), "agent"),
    capturedAt: asString(read(value, "capturedAt", "CapturedAt"), new Date().toISOString()),
    cpuPercent: asNumber(read(value, "cpuPercent", "CpuPercent"), 0),
    memoryUsedPercent: asNumber(read(value, "memoryUsedPercent", "MemoryUsedPercent"), 0),
    servicesOnline: asNumber(read(value, "servicesOnline", "ServicesOnline"), 0),
    servicesDegraded: asNumber(read(value, "servicesDegraded", "ServicesDegraded"), 0),
    servicesOffline: asNumber(read(value, "servicesOffline", "ServicesOffline"), 0)
  };
}

function normalizeService(raw: unknown): ServiceCard {
  const value = asRecord(raw);
  return {
    id: asString(read(value, "id", "Id"), "service"),
    name: asString(read(value, "name", "Name"), "Service"),
    kind: normalizeKind(read(value, "kind", "Kind")),
    description: asString(read(value, "description", "Description"), ""),
    url: asOptionalString(read(value, "url", "Url")),
    status: normalizeStatus(read(value, "status", "Status")),
    restartEnabled: Boolean(read(value, "restartEnabled", "RestartEnabled")),
    lastCheckedAt: asOptionalString(read(value, "lastCheckedAt", "LastCheckedAt")),
    statusMessage: asOptionalString(read(value, "statusMessage", "StatusMessage")),
    metrics: asArray<ServiceMetric>(read(value, "metrics", "Metrics"), (metric) => {
      const item = asRecord(metric);
      return {
        label: asString(read(item, "label", "Label"), "Metric"),
        value: asString(read(item, "value", "Value"), "")
      };
    })
  };
}

function normalizeSystem(raw: unknown): SystemStats {
  const value = asRecord(raw);
  return {
    hostname: asString(read(value, "hostname", "Hostname"), "Local host"),
    cpuPercent: asNumber(read(value, "cpuPercent", "CpuPercent"), 0),
    memoryUsedPercent: asNumber(read(value, "memoryUsedPercent", "MemoryUsedPercent"), 0),
    disks: asArray(read(value, "disks", "Disks"), (disk) => {
      const item = asRecord(disk);
      return {
        name: asString(read(item, "name", "Name"), "Disk"),
        totalBytes: asNumber(read(item, "totalBytes", "TotalBytes"), 0),
        freeBytes: asNumber(read(item, "freeBytes", "FreeBytes"), 0)
      };
    }),
    capturedAt: asString(read(value, "capturedAt", "CapturedAt"), new Date().toISOString()),
    uptimeSeconds: asNumber(read(value, "uptimeSeconds", "UptimeSeconds"), 0),
    osVersion: asOptionalString(read(value, "osVersion", "OsVersion")),
    pendingReboot: Boolean(read(value, "pendingReboot", "PendingReboot")),
    networkReceiveBytesPerSecond: asNumber(read(value, "networkReceiveBytesPerSecond", "NetworkReceiveBytesPerSecond"), 0),
    networkSendBytesPerSecond: asNumber(read(value, "networkSendBytesPerSecond", "NetworkSendBytesPerSecond"), 0),
    topProcesses: asArray(read(value, "topProcesses", "TopProcesses"), (process) => {
      const item = asRecord(process);
      return {
        processId: asNumber(read(item, "processId", "ProcessId"), 0),
        name: asString(read(item, "name", "Name"), "Process"),
        workingSetBytes: asNumber(read(item, "workingSetBytes", "WorkingSetBytes"), 0),
        cpuTime: asString(read(item, "cpuTime", "CpuTime"), "00:00:00")
      };
    })
  };
}

function normalizeNewsItem(raw: unknown): NewsItem {
  const value = asRecord(raw);
  return {
    source: asString(read(value, "source", "Source"), "News"),
    title: asString(read(value, "title", "Title"), "Untitled"),
    url: asOptionalString(read(value, "url", "Url")),
    publishedAt: asOptionalString(read(value, "publishedAt", "PublishedAt")),
    summary: asOptionalString(read(value, "summary", "Summary")),
    kind: normalizeNewsKind(read(value, "kind", "Kind")),
    category: asString(read(value, "category", "Category"), "Technology"),
    providerUrl: asOptionalString(read(value, "providerUrl", "ProviderUrl")),
    mediaUrl: asOptionalString(read(value, "mediaUrl", "MediaUrl")),
    imageUrl: asOptionalString(read(value, "imageUrl", "ImageUrl")),
    duration: asOptionalString(read(value, "duration", "Duration"))
  };
}

function normalizeNotification(raw: unknown): DashboardNotification {
  const value = asRecord(raw);
  return {
    id: asString(read(value, "id", "Id"), newId()),
    severity: normalizeSeverity(read(value, "severity", "Severity")),
    title: asString(read(value, "title", "Title"), "Alert"),
    message: asString(read(value, "message", "Message"), ""),
    createdAt: asString(read(value, "createdAt", "CreatedAt"), new Date().toISOString())
  };
}

function normalizeAuditEvent(raw: unknown): AuditEvent {
  const value = asRecord(raw);
  return {
    id: asString(read(value, "id", "Id"), newId()),
    type: asString(read(value, "type", "Type"), "Event"),
    message: asString(read(value, "message", "Message"), ""),
    serviceId: asOptionalString(read(value, "serviceId", "ServiceId")),
    agentId: asOptionalString(read(value, "agentId", "AgentId")),
    actor: asString(read(value, "actor", "Actor"), "system"),
    occurredAt: asString(read(value, "occurredAt", "OccurredAt"), new Date().toISOString()),
    commandId: asOptionalString(read(value, "commandId", "CommandId")),
    succeeded: read(value, "succeeded", "Succeeded") !== false
  };
}

function normalizeStatus(value: unknown): ServiceStatus {
  return pickEnum(value, ["Unknown", "Online", "Degraded", "Offline"], "Unknown");
}

function normalizeKind(value: unknown): ServiceKind {
  return pickEnum(value, ["Generic", "Plex", "Sonarr", "Radarr", "Lidarr", "Readarr", "Prowlarr", "Bazarr", "qBittorrent", "SABnzbd", "Jellyfin", "GameServer", "FileShare"], "Generic");
}

function normalizeSeverity(value: unknown): DashboardNotification["severity"] {
  return pickEnum(value, ["Info", "Warning", "Critical"], "Info");
}

function normalizeNewsKind(value: unknown): NewsContentKind {
  return pickEnum(value, ["Article", "Podcast"], "Article");
}

function pickEnum<T extends string>(value: unknown, values: T[], fallback: T): T {
  if (typeof value === "number" && values[value]) {
    return values[value];
  }

  if (typeof value === "string" && values.includes(value as T)) {
    return value as T;
  }

  return fallback;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value && typeof value === "object" ? value as Record<string, unknown> : {};
}

function read(value: Record<string, unknown>, camel: string, pascal: string): unknown {
  return value[camel] ?? value[pascal];
}

function asArray<T>(value: unknown, map: (item: unknown) => T): T[] {
  return Array.isArray(value) ? value.map(map) : [];
}

function asString(value: unknown, fallback: string): string {
  return typeof value === "string" && value.length > 0 ? value : fallback;
}

function asOptionalString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function asNumber(value: unknown, fallback: number): number {
  return typeof value === "number" && Number.isFinite(value) ? value : fallback;
}

function newId(): string {
  return `generated-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}
