import { Clapperboard, Download, ExternalLink, Film, Folder, Gamepad2, HardDrive, Radio, RotateCcw, Server, Tv, Workflow } from "lucide-react";
import type { ServiceCard, ServiceStatus } from "../types/dashboard";

interface Props {
  services: ServiceCard[];
  onRestart: (serviceId: string) => void;
}

const statusClass: Record<ServiceStatus, string> = {
  Online: "status online",
  Degraded: "status degraded",
  Offline: "status offline",
  Unknown: "status unknown"
};

const serviceIcons = {
  Plex: Film,
  Jellyfin: Clapperboard,
  Sonarr: Tv,
  Radarr: Film,
  Lidarr: Radio,
  Readarr: Folder,
  Prowlarr: Workflow,
  Bazarr: Workflow,
  qBittorrent: Download,
  SABnzbd: Download,
  GameServer: Gamepad2,
  FileShare: HardDrive,
  Generic: Server
};

export function ServiceGrid({ services, onRestart }: Props) {
  const items = Array.isArray(services) ? services : [];
  return (
    <section className="panel services-panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">Configured apps</span>
          <h2>Services</h2>
        </div>
        <span>{items.length} configured</span>
      </div>
      <div className="service-grid">
        {items.map((service) => {
          const Icon = serviceIcons[service.kind] ?? Server;
          const metrics = Array.isArray(service.metrics) ? service.metrics : [];

          return (
          <article className={`service-card ${service.status.toLowerCase()}`} key={service.id}>
            <div>
              <div className="service-title">
                <div className="service-heading">
                  <span className="service-icon">
                    <Icon size={20} />
                  </span>
                  <div>
                    <h3>{service.name}</h3>
                    <p>{service.description || service.kind}</p>
                  </div>
                </div>
                <span className={statusClass[service.status] ?? statusClass.Unknown}>
                  <span />
                  {service.status}
                </span>
              </div>
            </div>
            <div className="service-meta">{service.statusMessage ?? "Waiting for check."}</div>
            <div className="metric-strip">
              <span className="kind-chip">
                <Server size={13} />
                {service.kind}
              </span>
              {metrics.map((metric) => (
                <span className="metric-chip" key={`${service.id}-${metric.label}`}>
                  <b>{metric.label}</b>
                  {metric.value}
                </span>
              ))}
              {metrics.length === 0 ? <span className="metric-chip muted">No metrics yet</span> : null}
            </div>
            <div className="service-actions">
              {service.url ? (
                <a className="icon-button" href={service.url} target="_blank" rel="noreferrer" title="Open service">
                  <ExternalLink size={18} />
                </a>
              ) : (
                <span className="icon-button disabled" title="No link configured">
                  <ExternalLink size={18} />
                </span>
              )}
              <button
                className="icon-button"
                type="button"
                title={service.restartEnabled ? "Restart service" : "Restart disabled"}
                disabled={!service.restartEnabled}
                onClick={() => onRestart(service.id)}
              >
                <RotateCcw size={18} />
              </button>
            </div>
          </article>
          );
        })}
      </div>
    </section>
  );
}
