import type { AgentHistoryPoint, AuditEvent, DashboardNotification, DashboardSettings, DashboardSnapshot, NewsContentKind, NewsItem, ServiceCard, ServiceKind, ServiceMetric, ServiceStatus, SetupRequest, SetupStatus, SystemStats, UpdateDashboardSettingsRequest } from "../types/dashboard";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "";

export class ApiError extends Error {
  public constructor(public readonly status: number, message: string) {
    super(message);
    this.name = "ApiError";
  }
}

export interface AuthSession {
  isAuthenticated: boolean;
  expiresAt?: string | null;
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

export async function login(password: string): Promise<AuthSession> {
  const response = await fetch(`${apiBaseUrl}/auth/login`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password })
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

export async function getDashboard(): Promise<DashboardSnapshot> {
  const response = await fetch(`${apiBaseUrl}/api/dashboard`, { credentials: "include" });
  if (!response.ok) {
    throw new ApiError(response.status, `Dashboard request failed with ${response.status}`);
  }

  return normalizeDashboard(await response.json());
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
    capturedAt: asString(read(value, "capturedAt", "CapturedAt"), new Date().toISOString())
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
    providerUrl: asOptionalString(read(value, "providerUrl", "ProviderUrl"))
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
