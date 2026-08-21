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
