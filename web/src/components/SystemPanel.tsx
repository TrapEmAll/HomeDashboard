import { ArrowDown, ArrowUp, Cpu, HardDrive, MemoryStick, RotateCcw, Server, Timer, TrendingUp } from "lucide-react";
import type { AgentHistoryPoint, SystemStats } from "../types/dashboard";

interface Props {
  system?: SystemStats | null;
  history?: AgentHistoryPoint[];
}

export function SystemPanel({ system, history = [] }: Props) {
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
      <div className="host-facts">
        <span><Timer size={13} /><b>Uptime</b>{formatUptime(stats.uptimeSeconds ?? 0)}</span>
        <span><ArrowDown size={13} /><b>Receive</b>{formatRate(stats.networkReceiveBytesPerSecond ?? 0)}</span>
        <span><ArrowUp size={13} /><b>Send</b>{formatRate(stats.networkSendBytesPerSecond ?? 0)}</span>
        {stats.pendingReboot ? <span className="pending-reboot"><RotateCcw size={13} /><b>Windows</b>Restart pending</span> : null}
      </div>
      {history.length > 1 ? (
        <div className="telemetry-history">
          <div className="telemetry-history-heading"><TrendingUp size={14} /><span>Recent agent history</span><small>{history.length} samples</small></div>
          <Trend label="CPU" values={history.map((point) => point.cpuPercent)} tone="cyan" />
          <Trend label="Memory" values={history.map((point) => point.memoryUsedPercent)} tone="green" />
        </div>
      ) : null}
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
      {stats.topProcesses?.length ? <div className="process-list">
        <div className="process-list-heading"><span>Top processes</span><small>{stats.osVersion ?? "Windows"}</small></div>
        {stats.topProcesses.slice(0, 5).map((process) => <div key={process.processId}><strong>{process.name}</strong><span>PID {process.processId}</span><b>{formatBytes(process.workingSetBytes)}</b></div>)}
      </div> : null}
    </section>
  );
}

function Trend({ label, values, tone }: { label: string; values: number[]; tone: "cyan" | "green" }) {
  const samples = values.slice(-36).map(clampPercent);
  const points = samples.map((value, index) => `${samples.length === 1 ? 0 : index / (samples.length - 1) * 100},${28 - value / 100 * 26}`).join(" ");
  const current = samples.at(-1) ?? 0;
  return (
    <div className={`telemetry-trend ${tone}`}>
      <span>{label}</span>
      <svg viewBox="0 0 100 28" preserveAspectRatio="none" aria-label={`${label} history, currently ${formatPercent(current)} percent`}>
        <polyline points={points} vectorEffect="non-scaling-stroke" />
      </svg>
      <strong>{formatPercent(current)}%</strong>
    </div>
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

function formatRate(bytes: number): string {
  if (bytes < 1024) return `${bytes.toFixed(0)} B/s`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB/s`;
  return `${(bytes / 1024 / 1024).toFixed(1)} MB/s`;
}

function formatUptime(seconds: number): string {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor(seconds % 86400 / 3600);
  return days > 0 ? `${days}d ${hours}h` : `${hours}h`;
}
