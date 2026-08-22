import { Cpu, HardDrive, MemoryStick } from "lucide-react";
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

  return (
    <section className="panel">
      <div className="section-heading">
        <h2>{stats.hostname}</h2>
        <span>{new Date(stats.capturedAt).toLocaleTimeString()}</span>
      </div>
      <div className="stat-grid">
        <Metric icon={<Cpu size={19} />} label="CPU" value={`${formatPercent(stats.cpuPercent)}%`} />
        <Metric icon={<MemoryStick size={19} />} label="Memory" value={`${formatPercent(stats.memoryUsedPercent)}%`} />
        <Metric icon={<HardDrive size={19} />} label="Disks" value={`${disks.length}`} />
      </div>
      <div className="disk-list">
        {disks.map((disk) => {
          const usedPercent = disk.totalBytes > 0 ? ((disk.totalBytes - disk.freeBytes) / disk.totalBytes) * 100 : 0;
          return (
            <div className="disk-row" key={disk.name}>
              <span>{disk.name}</span>
              <div className="meter">
                <div style={{ width: `${Math.min(usedPercent, 100)}%` }} />
              </div>
              <span>{usedPercent.toFixed(0)}%</span>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function Metric({ icon, label, value }: { icon: React.ReactNode; label: string; value: string }) {
  return (
    <div className="metric">
      {icon}
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  );
}

function formatPercent(value: number): string {
  return Number.isFinite(value) ? value.toFixed(1) : "0.0";
}
