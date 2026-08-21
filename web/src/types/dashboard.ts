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
}

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
