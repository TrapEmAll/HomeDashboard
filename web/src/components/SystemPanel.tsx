import { Cpu, HardDrive, MemoryStick, Server } from "lucide-react";
import type { SystemStats } from "../types/dashboard";

interface Props {
  system?: SystemStats | null;
}

export function SystemPanel({ system }: Props) {
  const stats = system ?? {
    hostname: "Local host",
    cpuPercent: 0,
    memoryUsedPercent: 0,
    disks: [],
    capturedAt: new Date().toISOString()
  };
  const disks = Array.isArray(stats.disks) ? stats.disks : [];
  const cpuPercent = clampPercent(stats.cpuPercent);
  const memoryPercent = clampPercent(stats.memoryUsedPercent);

  return (
    <section className="panel system-panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">Host telemetry</span>
          <h2>{stats.hostname}</h2>
        </div>
        <span>{new Date(stats.capturedAt).toLocaleTimeString()}</span>
      </div>
      <div className="stat-grid">
        <Metric icon={<Cpu size={19} />} label="CPU" value={`${formatPercent(cpuPercent)}%`} tone="blue" />
        <Metric icon={<MemoryStick size={19} />} label="Memory" value={`${formatPercent(memoryPercent)}%`} tone="green" />
        <Metric icon={<HardDrive size={19} />} label="Disks" value={`${disks.length}`} tone="amber" />
      </div>
      <div className="disk-list">
        {disks.map((disk) => {
          const usedPercent = clampPercent(disk.totalBytes > 0 ? ((disk.totalBytes - disk.freeBytes) / disk.totalBytes) * 100 : 0);
          return (
            <div className={`disk-row ${usedPercent >= 90 ? "critical" : usedPercent >= 80 ? "warning" : ""}`} key={disk.name}>
              <span className="disk-name"><Server size={13} /> {disk.name}</span>
              <div className="meter">
                <div style={{ width: `${Math.min(usedPercent, 100)}%` }} />
              </div>
              <span title={`${formatBytes(disk.freeBytes)} free of ${formatBytes(disk.totalBytes)}`}>{formatBytes(disk.freeBytes)} free</span>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function Metric({ icon, label, value, tone }: { icon: React.ReactNode; label: string; value: string; tone: string }) {
  return (
    <div className={`metric ${tone}`}>
      <div className="metric-icon">{icon}</div>
      <div>
        <span>{label}</span>
        <strong>{value}</strong>
      </div>
    </div>
  );
}

function formatPercent(value: number): string {
  return Number.isFinite(value) ? value.toFixed(1) : "0.0";
}

function clampPercent(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.min(value, 100)) : 0;
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "0 GB";
  }

  const gigabytes = bytes / 1024 / 1024 / 1024;
  return gigabytes >= 1024 ? `${(gigabytes / 1024).toFixed(1)} TB` : `${gigabytes.toFixed(0)} GB`;
}
