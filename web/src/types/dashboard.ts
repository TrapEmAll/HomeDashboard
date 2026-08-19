export type ServiceStatus = "Unknown" | "Online" | "Degraded" | "Offline";

export interface DashboardSnapshot {
  generatedAt: string;
  services: ServiceCard[];
  system: SystemStats;
  news: NewsItem[];
}

export interface ServiceCard {
  id: string;
  name: string;
  description: string;
  url?: string | null;
  status: ServiceStatus;
  restartEnabled: boolean;
  lastCheckedAt?: string | null;
  statusMessage?: string | null;
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
