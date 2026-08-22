import { Clapperboard, Download, ExternalLink, Film, Folder, Gamepad2, HardDrive, Radio, RotateCcw, Server, Star, Tv, Workflow } from "lucide-react";
import type { ServiceCard, ServiceKind, ServiceStatus } from "../types/dashboard";

interface Props {
  services: ServiceCard[];
  favorites: string[];
  onToggleFavorite: (serviceId: string) => void;
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

const serviceGroups: Array<{ title: string; kinds: ServiceKind[] }> = [
  { title: "Media", kinds: ["Plex", "Jellyfin", "Sonarr", "Radarr", "Lidarr", "Readarr", "Bazarr"] },
  { title: "Services", kinds: ["qBittorrent", "SABnzbd", "Prowlarr", "Generic"] },
  { title: "Networking", kinds: ["FileShare"] },
  { title: "Utilities", kinds: ["GameServer"] }
];

export function ServiceGrid({ services, favorites, onToggleFavorite, onRestart }: Props) {
  const items = Array.isArray(services) ? services : [];
  const favoriteSet = new Set(favorites);
  const grouped = groupServices([...items].sort((left, right) => Number(favoriteSet.has(right.id)) - Number(favoriteSet.has(left.id)) || left.name.localeCompare(right.name)));

  return (
    <section className="panel services-panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">Configured apps</span>
          <h2>Services</h2>
        </div>
        <span>{items.length} visible</span>
      </div>
      {items.length === 0 ? <div className="service-filter-empty">No services match this view.</div> : <div className="service-columns">
        {grouped.map((group) => (
          <div className="service-column" key={group.title}>
            <div className="service-column-heading">
              <h3>{group.title}</h3>
              <span>{group.services.length}</span>
            </div>
            <div className="service-list">
              {group.services.length > 0 ? group.services.map((service) => {
                const Icon = serviceIcons[service.kind] ?? Server;
                const metrics = Array.isArray(service.metrics) ? service.metrics : [];

                return (
                  <article className={`service-row ${service.status.toLowerCase()}`} key={service.id}>
                    <span className="service-icon">
                      <Icon size={22} />
                    </span>
                    <div className="service-row-main">
                      <div className="service-row-title">
                        <strong>{service.name}</strong>
                        <span
                          className={statusClass[service.status] ?? statusClass.Unknown}
                          title={service.statusMessage ? `${service.status}: ${service.statusMessage}` : service.status}
                        >
                          <span />
                        </span>
                      </div>
                      <p>{service.description || service.kind}</p>
                      {service.statusMessage ? (
                        <span className="service-row-message" title={service.statusMessage}>
                          {service.statusMessage}
                        </span>
                      ) : null}
                      <div className="metric-strip">
                        {metrics.slice(0, 2).map((metric) => (
                          <span className="metric-chip" key={`${service.id}-${metric.label}`}>
                            <b>{metric.label}</b>
                            {metric.value}
                          </span>
                        ))}
                      </div>
                    </div>
                    <div className="service-actions">
                      <button
                        className={`icon-button favorite-button ${favoriteSet.has(service.id) ? "selected" : ""}`}
                        type="button"
                        title={favoriteSet.has(service.id) ? "Remove favorite" : "Add favorite"}
                        aria-pressed={favoriteSet.has(service.id)}
                        onClick={() => onToggleFavorite(service.id)}
                      >
                        <Star size={16} fill={favoriteSet.has(service.id) ? "currentColor" : "none"} />
                      </button>
                      {service.url ? (
                        <a className="icon-button" href={service.url} target="_blank" rel="noreferrer" title="Open service">
                          <ExternalLink size={17} />
                        </a>
                      ) : null}
                      <button
                        className="icon-button"
                        type="button"
                        title={service.restartEnabled ? "Restart service" : "Restart disabled"}
                        disabled={!service.restartEnabled}
                        onClick={() => onRestart(service.id)}
                      >
                        <RotateCcw size={17} />
                      </button>
                    </div>
                  </article>
                );
              }) : (
                <span className="column-empty">No services yet</span>
              )}
            </div>
          </div>
        ))}
      </div>}
    </section>
  );
}

function groupServices(services: ServiceCard[]) {
  const assigned = new Set<string>();
  const groups = serviceGroups.map((group) => {
    const groupServices = services.filter((service) => {
      const matches = group.kinds.includes(service.kind);
      if (matches) {
        assigned.add(service.id);
      }

      return matches;
    });

    return {
      title: group.title,
      services: groupServices
    };
  });

  const otherServices = services.filter((service) => !assigned.has(service.id));
  if (otherServices.length > 0) {
    groups.push({ title: "Other", services: otherServices });
  }

return groups.filter((group) => group.services.length > 0 || group.title !== "Other");
}
