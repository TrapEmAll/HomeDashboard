import { Activity, ArrowDown, ArrowUp, Cable, Gauge, Radio, TriangleAlert, Wifi } from "lucide-react";
import type { NetworkInterfaceStats, SystemStats } from "../types/dashboard";

interface Props {
  system?: SystemStats | null;
}

export function NetworkPanel({ system }: Props) {
  const interfaces = (system?.networkInterfaces ?? []).filter((item) => item.linkSpeedBitsPerSecond > 0 || item.address);
  const probe = system?.networkProbe;
  const receive = system?.networkReceiveBytesPerSecond ?? interfaces.reduce((sum, item) => sum + item.receiveBytesPerSecond, 0);
  const send = system?.networkSendBytesPerSecond ?? interfaces.reduce((sum, item) => sum + item.sendBytesPerSecond, 0);
  const packets = interfaces.reduce((sum, item) => sum + item.receivePacketsPerSecond + item.sendPacketsPerSecond, 0);
  const errors = interfaces.reduce((sum, item) => sum + item.incomingErrors + item.outgoingErrors + item.incomingDiscards + item.outgoingDiscards, 0);

  return (
    <section className="panel network-panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">API host network</span>
          <h2>{system?.hostname ?? "Local network"}</h2>
        </div>
        <span className={`network-signal ${probe && probe.packetLossPercent === 0 ? "healthy" : probe ? "warning" : "pending"}`}>
          <Radio size={12} /> {probe ? `${probe.packetLossPercent.toFixed(1)}% loss` : "Sampling"}
        </span>
      </div>

      <div className="network-overview">
        <NetworkMetric icon={<ArrowDown size={16} />} label="Down" value={formatRate(receive)} tone="cyan" />
        <NetworkMetric icon={<ArrowUp size={16} />} label="Up" value={formatRate(send)} tone="green" />
        <NetworkMetric icon={<Activity size={16} />} label="Packets" value={`${formatNumber(packets)}/s`} tone="violet" />
        <NetworkMetric icon={<Gauge size={16} />} label="Latency" value={probe?.averageLatencyMilliseconds != null ? `${probe.averageLatencyMilliseconds.toFixed(1)} ms` : "--"} tone="amber" />
      </div>

      {probe ? (
        <div className="probe-strip" title={`Last probe ${new Date(probe.sampledAt).toLocaleString()}`}>
          <Wifi size={13} />
          <span>Gateway {probe.target}</span>
          <b>{probe.received}/{probe.sent} replies</b>
          <small>{formatLatencyRange(probe.minimumLatencyMilliseconds, probe.maximumLatencyMilliseconds)}</small>
        </div>
      ) : null}

      <div className="network-interface-list">
        {interfaces.length ? interfaces.map((item) => <InterfaceRow key={item.id} item={item} />) : (
          <div className="network-empty"><Cable size={16} /><span>Waiting for an active interface sample.</span></div>
        )}
      </div>

      {errors > 0 ? <div className="network-errors"><TriangleAlert size={13} />{formatNumber(errors)} lifetime errors or discarded packets</div> : null}
    </section>
  );
}

function InterfaceRow({ item }: { item: NetworkInterfaceStats }) {
  const totalBytes = item.receiveBytesPerSecond + item.sendBytesPerSecond;
  const utilization = item.linkSpeedBitsPerSecond > 0
    ? Math.min(100, totalBytes * 8 / item.linkSpeedBitsPerSecond * 100)
    : 0;

  return (
    <div className="network-interface">
      <div className="network-interface-title">
        <span className="interface-icon"><Cable size={14} /></span>
        <div><strong>{item.name}</strong><span>{item.address ?? item.interfaceType}</span></div>
        <b>{formatLinkSpeed(item.linkSpeedBitsPerSecond)}</b>
      </div>
      <div className="network-utilization"><span style={{ width: `${Math.max(utilization, totalBytes > 0 ? 1.5 : 0)}%` }} /></div>
      <div className="network-interface-stats">
        <span><ArrowDown size={11} />{formatRate(item.receiveBytesPerSecond)}</span>
        <span><ArrowUp size={11} />{formatRate(item.sendBytesPerSecond)}</span>
        <span>{formatNumber(item.receivePacketsPerSecond + item.sendPacketsPerSecond)} pps</span>
        <span>{utilization.toFixed(utilization >= 1 ? 1 : 2)}% link</span>
      </div>
    </div>
  );
}

function NetworkMetric({ icon, label, value, tone }: { icon: React.ReactNode; label: string; value: string; tone: string }) {
  return <div className={`network-metric ${tone}`}><span>{icon}{label}</span><strong>{value}</strong></div>;
}

function formatRate(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 1024) return `${Math.max(0, bytes).toFixed(0)} B/s`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB/s`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB/s`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB/s`;
}

function formatLinkSpeed(bits: number): string {
  if (bits >= 1_000_000_000) return `${(bits / 1_000_000_000).toFixed(bits % 1_000_000_000 === 0 ? 0 : 1)} Gbps`;
  if (bits >= 1_000_000) return `${(bits / 1_000_000).toFixed(0)} Mbps`;
  return bits > 0 ? `${(bits / 1_000).toFixed(0)} Kbps` : "Unknown";
}

function formatNumber(value: number): string {
  return new Intl.NumberFormat(undefined, { maximumFractionDigits: value < 100 ? 1 : 0 }).format(value);
}

function formatLatencyRange(min?: number | null, max?: number | null): string {
  return min == null || max == null ? "No latency reply" : `${min.toFixed(0)}-${max.toFixed(0)} ms`;
}
