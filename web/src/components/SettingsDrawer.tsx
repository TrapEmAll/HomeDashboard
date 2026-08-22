import { CheckCircle2, KeyRound, Plus, Radio, Rss, Save, Server, Trash2, X } from "lucide-react";
import { useState } from "react";
import type { FormEvent } from "react";
import type { DashboardSettings, NewsContentKind, NewsFeedSetting, ServiceKind, UpdateDashboardSettingsRequest } from "../types/dashboard";

interface Props {
  settings: DashboardSettings;
  saving: boolean;
  error?: string | null;
  onClose: () => void;
  onSave: (request: UpdateDashboardSettingsRequest) => Promise<void>;
}

type EditableService = DashboardSettings["services"][number] & {
  apiKey: string;
  clearApiKey: boolean;
};

const serviceKinds: ServiceKind[] = ["Generic", "Plex", "Sonarr", "Radarr", "Lidarr", "Readarr", "Prowlarr", "Bazarr", "qBittorrent", "SABnzbd", "Jellyfin", "GameServer", "FileShare"];

export function SettingsDrawer({ settings, saving, error, onClose, onSave }: Props) {
  const [tab, setTab] = useState<"services" | "feeds">("services");
  const [defaultAgentId, setDefaultAgentId] = useState(settings.defaultAgentId);
  const [includeRecommendedFeeds, setIncludeRecommendedFeeds] = useState(settings.includeRecommendedFeeds);
  const [services, setServices] = useState<EditableService[]>(() => settings.services.map((service) => ({ ...service, apiKey: "", clearApiKey: false })));
  const [feeds, setFeeds] = useState<NewsFeedSetting[]>(() => settings.newsFeeds.map((feed) => ({ ...feed })));

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await onSave({
      defaultAgentId,
      includeRecommendedFeeds,
      services: services.map(({ hasApiKey: _hasApiKey, ...service }) => service),
      newsFeeds: feeds
    });
  }

  function updateService(index: number, patch: Partial<EditableService>) {
    setServices((current) => current.map((service, candidate) => candidate === index ? { ...service, ...patch } : service));
  }

  function addService() {
    const number = services.length + 1;
    setServices((current) => [...current, {
      id: `service-${number}`,
      name: "New service",
      kind: "Generic",
      description: "",
      url: "",
      healthUrl: "",
      hasApiKey: false,
      apiKey: "",
      clearApiKey: false,
      restartEnabled: false
    }]);
  }

  function updateFeed(index: number, patch: Partial<NewsFeedSetting>) {
    setFeeds((current) => current.map((feed, candidate) => candidate === index ? { ...feed, ...patch } : feed));
  }

  function addFeed() {
    setFeeds((current) => [...current, { name: "New feed", url: "", kind: "Article", category: "Technology", providerUrl: "" }]);
  }

  return (
    <div className="settings-backdrop" role="presentation">
      <form className="settings-drawer" role="dialog" aria-modal="true" aria-labelledby="settings-title" onSubmit={(event) => void submit(event)}>
        <header className="settings-header">
          <div>
            <span className="section-kicker">Authenticated configuration</span>
            <h2 id="settings-title">Dashboard settings</h2>
          </div>
          <button className="icon-button" type="button" onClick={onClose} title="Close settings"><X size={19} /></button>
        </header>

        <div className="settings-foundation">
          <label>
            <span>Default agent ID</span>
            <input value={defaultAgentId} onChange={(event) => setDefaultAgentId(event.target.value)} required />
          </label>
          <label className="switch-row">
            <input type="checkbox" checked={includeRecommendedFeeds} onChange={(event) => setIncludeRecommendedFeeds(event.target.checked)} />
            <span>
              <strong>Recommended intelligence feeds</strong>
              <small>Include the built-in technology and podcast catalog.</small>
            </span>
          </label>
        </div>

        <div className="settings-tabs" role="tablist" aria-label="Settings sections">
          <button className={tab === "services" ? "active" : ""} type="button" role="tab" aria-selected={tab === "services"} onClick={() => setTab("services")}>
            <Server size={16} /> Services <span>{services.length}</span>
          </button>
          <button className={tab === "feeds" ? "active" : ""} type="button" role="tab" aria-selected={tab === "feeds"} onClick={() => setTab("feeds")}>
            <Rss size={16} /> Custom feeds <span>{feeds.length}</span>
          </button>
        </div>

        <div className="settings-scroll">
          {tab === "services" ? (
            <div className="settings-records">
              {services.map((service, index) => (
                <section className="settings-record" key={`${service.id}-${index}`}>
                  <div className="settings-record-heading">
                    <div className="record-icon"><Server size={17} /></div>
                    <div><strong>{service.name || "Unnamed service"}</strong><span>{service.id || "Service ID required"}</span></div>
                    <button className="danger-icon" type="button" onClick={() => setServices((current) => current.filter((_, candidate) => candidate !== index))} title={`Remove ${service.name}`}><Trash2 size={16} /></button>
                  </div>
                  <div className="settings-grid three">
                    <label><span>Name</span><input value={service.name} onChange={(event) => updateService(index, { name: event.target.value })} required /></label>
                    <label><span>Service ID</span><input value={service.id} onChange={(event) => updateService(index, { id: event.target.value })} required /></label>
                    <label><span>Type</span><select value={service.kind} onChange={(event) => updateService(index, { kind: event.target.value as ServiceKind })}>{serviceKinds.map((kind) => <option key={kind}>{kind}</option>)}</select></label>
                  </div>
                  <div className="settings-grid two">
                    <label><span>Open URL</span><input type="url" value={service.url ?? ""} onChange={(event) => updateService(index, { url: event.target.value })} placeholder="http://server-pc:32400" /></label>
                    <label><span>Health check URL</span><input type="url" value={service.healthUrl ?? ""} onChange={(event) => updateService(index, { healthUrl: event.target.value })} placeholder="http://server-pc:32400/identity" /></label>
                  </div>
                  <label className="wide-field"><span>Description</span><input value={service.description} onChange={(event) => updateService(index, { description: event.target.value })} placeholder="What this service does" /></label>
                  <div className="settings-grid two align-end">
                    <label>
                      <span><KeyRound size={13} /> API key {service.hasApiKey && !service.clearApiKey ? <b>Saved</b> : null}</span>
                      <input type="password" value={service.apiKey} disabled={service.clearApiKey} onChange={(event) => updateService(index, { apiKey: event.target.value })} placeholder={service.hasApiKey ? "Leave blank to keep saved key" : "Optional"} />
                    </label>
                    <div className="service-switches">
                      {service.hasApiKey ? <label className="compact-check"><input type="checkbox" checked={service.clearApiKey} onChange={(event) => updateService(index, { clearApiKey: event.target.checked, apiKey: "" })} /> Clear saved key</label> : null}
                      <label className="compact-check"><input type="checkbox" checked={service.restartEnabled} onChange={(event) => updateService(index, { restartEnabled: event.target.checked })} /> Allow restart controls</label>
                    </div>
                  </div>
                </section>
              ))}
              <button className="add-record" type="button" onClick={addService}><Plus size={17} /> Add service</button>
            </div>
          ) : (
            <div className="settings-records">
              <div className="settings-note"><Radio size={17} /><span>These are your own sources. Built-in recommendations are controlled by the toggle above.</span></div>
              {feeds.map((feed, index) => (
                <section className="settings-record" key={`${feed.name}-${index}`}>
                  <div className="settings-record-heading">
                    <div className="record-icon feed"><Rss size={17} /></div>
                    <div><strong>{feed.name || "Unnamed feed"}</strong><span>{feed.kind} · {feed.category || "Uncategorized"}</span></div>
                    <button className="danger-icon" type="button" onClick={() => setFeeds((current) => current.filter((_, candidate) => candidate !== index))} title={`Remove ${feed.name}`}><Trash2 size={16} /></button>
                  </div>
                  <div className="settings-grid three">
                    <label><span>Name</span><input value={feed.name} onChange={(event) => updateFeed(index, { name: event.target.value })} required /></label>
                    <label><span>Content type</span><select value={feed.kind} onChange={(event) => updateFeed(index, { kind: event.target.value as NewsContentKind })}><option>Article</option><option>Podcast</option></select></label>
                    <label><span>Category</span><input value={feed.category} onChange={(event) => updateFeed(index, { category: event.target.value })} placeholder="Technology" /></label>
                  </div>
                  <label className="wide-field"><span>RSS or Atom URL</span><input type="url" value={feed.url} onChange={(event) => updateFeed(index, { url: event.target.value })} placeholder="https://example.com/feed.xml" required /></label>
                  <label className="wide-field"><span>Provider page</span><input type="url" value={feed.providerUrl ?? ""} onChange={(event) => updateFeed(index, { providerUrl: event.target.value })} placeholder="Optional show or publication page" /></label>
                </section>
              ))}
              <button className="add-record" type="button" onClick={addFeed}><Plus size={17} /> Add custom feed</button>
            </div>
          )}
        </div>

        <footer className="settings-footer">
          <div className="settings-save-state">
            {settings.requiresRestart ? <><CheckCircle2 size={17} /><span>Saved. Restart the API to apply these changes.</span></> : <span>API keys and passwords are never shown here.</span>}
            {error ? <strong>{error}</strong> : null}
          </div>
          <button className="secondary-button" type="button" onClick={onClose}>Close</button>
          <button className="primary-button" type="submit" disabled={saving}><Save size={17} /> {saving ? "Saving..." : "Save changes"}</button>
        </footer>
      </form>
    </div>
  );
}
