import { ExternalLink, RotateCcw } from "lucide-react";
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

export function ServiceGrid({ services, onRestart }: Props) {
  return (
    <section className="panel">
      <div className="section-heading">
        <h2>Services</h2>
        <span>{services.length} configured</span>
      </div>
      <div className="service-grid">
        {services.map((service) => (
          <article className="service-card" key={service.id}>
            <div>
              <div className="service-title">
                <h3>{service.name}</h3>
                <span className={statusClass[service.status]}>{service.status}</span>
              </div>
              <p>{service.description}</p>
            </div>
            <div className="service-meta">{service.statusMessage ?? "Waiting for check."}</div>
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
        ))}
      </div>
    </section>
  );
}
