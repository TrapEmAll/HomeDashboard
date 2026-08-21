import { Cpu, HardDrive, MemoryStick } from "lucide-react";
import type { SystemStats } from "../types/dashboard";

interface Props {
  system: SystemStats;
}

export function SystemPanel({ system }: Props) {
  return (
    <section className="panel">
      <div className="section-heading">
        <h2>{system.hostname}</h2>
        <span>{new Date(system.capturedAt).toLocaleTimeString()}</span>
      </div>
      <div className="stat-grid">
        <Metric icon={<Cpu size={19} />} label="CPU" value={`${system.cpuPercent.toFixed(1)}%`} />
        <Metric icon={<MemoryStick size={19} />} label="Memory" value={`${system.memoryUsedPercent.toFixed(1)}%`} />
        <Metric icon={<HardDrive size={19} />} label="Disks" value={`${system.disks.length}`} />
      </div>
      <div className="disk-list">
        {system.disks.map((disk) => {
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
