export type ServiceStatus = "Unknown" | "Online" | "Degraded" | "Offline";

export type ServiceKind =
  | "Generic"
  | "Plex"
  | "Sonarr"
  | "Radarr"
  | "Lidarr"
  | "Readarr"
  | "Prowlarr"
  | "Bazarr"
  | "qBittorrent"
  | "SABnzbd"
  | "Jellyfin"
  | "GameServer"
  | "FileShare";

export interface DashboardSnapshot {
  generatedAt: string;
  services: ServiceCard[];
  system: SystemStats;
  news: NewsItem[];
  agents: AgentSummary[];
  notifications: DashboardNotification[];
  recentAuditEvents: AuditEvent[];
}

export interface AgentSummary {
  agentId: string;
  hostname: string;
  lastSeenAt: string;
  status: ServiceStatus;
  servicesMonitored: number;
}

export interface AgentHistoryPoint {
  agentId: string;
  capturedAt: string;
  cpuPercent: number;
  memoryUsedPercent: number;
  servicesOnline: number;
  servicesDegraded: number;
  servicesOffline: number;
}

export interface ServiceCard {
  id: string;
  name: string;
  kind: ServiceKind;
  description: string;
  url?: string | null;
  status: ServiceStatus;
  restartEnabled: boolean;
  lastCheckedAt?: string | null;
  statusMessage?: string | null;
  metrics: ServiceMetric[];
}

export interface ServiceMetric {
  label: string;
  value: string;
}

export interface SystemStats {
  hostname: string;
  cpuPercent: number;
  memoryUsedPercent: number;
  disks: DiskStats[];
  capturedAt: string;
  uptimeSeconds?: number;
  osVersion?: string | null;
  pendingReboot?: boolean;
  networkReceiveBytesPerSecond?: number;
  networkSendBytesPerSecond?: number;
  topProcesses?: ProcessStats[] | null;
}

export interface ProcessStats {
  processId: number;
  name: string;
  workingSetBytes: number;
  cpuTime: string;
}

export interface DiskStats {
  name: string;
  totalBytes: number;
  freeBytes: number;
}

export interface NewsItem {
  source: string;
  title: string;
  url?: string | null;
  publishedAt?: string | null;
  summary?: string | null;
  kind: NewsContentKind;
  category: string;
  providerUrl?: string | null;
  mediaUrl?: string | null;
  imageUrl?: string | null;
  duration?: string | null;
}

export type NewsContentKind = "Article" | "Podcast";

export type NotificationSeverity = "Info" | "Warning" | "Critical";

export interface DashboardNotification {
  id: string;
  severity: NotificationSeverity;
  title: string;
  message: string;
  createdAt: string;
}

export interface AuditEvent {
  id: string;
  type: string;
  message: string;
  serviceId?: string | null;
  agentId?: string | null;
  actor: string;
  occurredAt: string;
  commandId?: string | null;
  succeeded: boolean;
}

export interface SetupStatus {
  isConfigured: boolean;
  usesPlaceholderSecrets: boolean;
  requiresRestart: boolean;
  defaultAgentId?: string | null;
  serviceCount: number;
  newsFeedCount: number;
}

export interface SetupRequest {
  dashboardPassword: string;
  dashboardApiKey?: string | null;
  agentApiKey?: string | null;
  defaultAgentId: string;
  services: ServiceSetupRequest[];
  newsFeeds: NewsFeedSetupRequest[];
}

export interface ServiceSetupRequest {
  id: string;
  name: string;
  kind: ServiceKind;
  description: string;
  url?: string | null;
  healthUrl?: string | null;
  apiKey?: string | null;
  restartEnabled: boolean;
}

export interface NewsFeedSetupRequest {
  name: string;
  url: string;
}

export interface DashboardSettings {
  defaultAgentId: string;
  includeRecommendedFeeds: boolean;
  services: ServiceSetting[];
  newsFeeds: NewsFeedSetting[];
  requiresRestart: boolean;
}

export interface ServiceSetting {
  id: string;
  name: string;
  kind: ServiceKind;
  description: string;
  url?: string | null;
  healthUrl?: string | null;
  hasApiKey: boolean;
  restartEnabled: boolean;
}

export interface NewsFeedSetting {
  name: string;
  url: string;
  kind: NewsContentKind;
  category: string;
  providerUrl?: string | null;
}

export interface UpdateDashboardSettingsRequest {
  defaultAgentId: string;
  includeRecommendedFeeds: boolean;
  services: UpdateServiceSetting[];
  newsFeeds: NewsFeedSetting[];
}

export interface UpdateServiceSetting {
  id: string;
  name: string;
  kind: ServiceKind;
  description: string;
  url?: string | null;
  healthUrl?: string | null;
  apiKey?: string | null;
  clearApiKey: boolean;
  restartEnabled: boolean;
}

export interface OpmlImportPreview {
  feeds: NewsFeedSetting[];
  feedOutlineCount: number;
  skippedCount: number;
}

export type OperationsActivityKind = "Service" | "Media" | "Download" | "Playback" | "Maintenance" | "Security" | "System";

export interface OperationsSnapshot {
  generatedAt: string;
  activity: OperationsActivity[];
  calendar: MediaCalendarItem[];
  playbackSessions: PlaybackSession[];
  downloads: DownloadQueueItem[];
  uptime: ServiceUptimeSummary[];
  storage: StorageForecast[];
  incidents: IncidentSummary[];
  maintenance: MaintenanceWindow[];
  update: UpdateSummary;
  arr: ArrOperationsSummary;
}

export interface ArrOperationsSummary {
  instances: ArrInstanceSummary[];
  queue: ArrQueueItem[];
  health: ArrHealthIssue[];
  history: ArrHistoryItem[];
}

export interface ArrInstanceSummary {
  serviceId: string;
  name: string;
  kind: ServiceKind;
  connected: boolean;
  version?: string | null;
  queueCount: number;
  healthIssueCount: number;
  missingCount: number;
}

export interface ArrQueueItem {
  id: string;
  serviceId: string;
  source: string;
  title: string;
  detail?: string | null;
  status: string;
  trackedStatus?: string | null;
  progressPercent: number;
  errorMessage?: string | null;
}

export interface ArrHealthIssue {
  id: string;
  serviceId: string;
  source: string;
  type: string;
  message: string;
}

export interface ArrHistoryItem {
  id: string;
  serviceId: string;
  source: string;
  title: string;
  eventType: string;
  occurredAt: string;
  quality?: string | null;
}

export type ArrCommandAction = "RefreshMonitoredDownloads" | "SearchMissing";

export interface ArrCommandResult {
  succeeded: boolean;
  requiresConfirmation: boolean;
  message: string;
}

export interface OperationsActivity {
  id: string;
  occurredAt: string;
  source: string;
  title: string;
  detail: string;
  kind: OperationsActivityKind;
  severity: NotificationSeverity;
}

export interface MediaCalendarItem {
  id: string;
  source: string;
  title: string;
  subtitle?: string | null;
  airsAt: string;
  mediaType: string;
  monitored: boolean;
  hasFile: boolean;
  url?: string | null;
}

export interface PlaybackSession {
  id: string;
  user: string;
  title: string;
  subtitle?: string | null;
  player: string;
  decision: string;
  progressPercent: number;
  videoResolution?: string | null;
  bandwidthKbps?: number | null;
}

export interface DownloadQueueItem {
  id: string;
  source: string;
  name: string;
  status: string;
  progressPercent: number;
  sizeBytes?: number | null;
  remainingBytes?: number | null;
  downloadSpeedBytes?: number | null;
  eta?: string | null;
  canPause: boolean;
  canRemove: boolean;
}

export type DownloadControlAction = "Pause" | "Resume" | "Recheck" | "Remove";

export interface ServiceUptimeSummary {
  serviceId: string;
  name: string;
  uptimePercent: number;
  windowStartedAt: string;
  incidentCount: number;
  currentStatus: ServiceStatus;
}

export interface StorageForecast {
  name: string;
  totalBytes: number;
  freeBytes: number;
  usedPercent: number;
  dailyGrowthBytes?: number | null;
  daysRemaining?: number | null;
}

export interface IncidentSummary {
  id: string;
  serviceId: string;
  serviceName: string;
  severity: NotificationSeverity;
  message: string;
  startedAt: string;
  resolvedAt?: string | null;
}

export interface MaintenanceWindow {
  id: string;
  title: string;
  startsAt: string;
  endsAt: string;
  serviceId?: string | null;
  suppressAlerts: boolean;
  createdBy: string;
}

export interface UpdateSummary {
  currentVersion: string;
  channel: string;
  repositoryUrl: string;
  lastCheckedAt?: string | null;
  updateAvailable: boolean;
  latestVersion?: string | null;
}

export interface DiscoveredService {
  id: string;
  name: string;
  kind: ServiceKind;
  url: string;
  port: number;
  alreadyConfigured: boolean;
}

export interface ServiceDiscoveryResult {
  services: DiscoveredService[];
  scannedAt: string;
}
