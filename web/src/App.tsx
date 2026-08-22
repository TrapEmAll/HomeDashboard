import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { Activity, Bell, CheckCircle2, Clock3, History, LogOut, RefreshCw, Server, ShieldCheck, Signal, TriangleAlert, Wand2 } from "lucide-react";
import { dashboardEventsUrl, getDashboard, getSession, getSetupStatus, login, logout, parseDashboardSnapshot, requestRestart, saveSetup } from "./lib/api";
import { NewsPanel } from "./components/NewsPanel";
import { ServiceGrid } from "./components/ServiceGrid";
import { SystemPanel } from "./components/SystemPanel";
import type { AuditEvent, DashboardNotification, DashboardSnapshot, ServiceKind, SetupRequest, SetupStatus } from "./types/dashboard";
import "./styles.css";

export function App() {
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [liveError, setLiveError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [authenticated, setAuthenticated] = useState(false);
  const [password, setPassword] = useState("");
  const [signingIn, setSigningIn] = useState(false);
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [setupSaving, setSetupSaving] = useState(false);
  const [setupForm, setSetupForm] = useState<SetupRequest>({
    dashboardPassword: "",
    dashboardApiKey: "",
    agentApiKey: "",
    defaultAgentId: "server-pc",
    services: [
      { id: "plex", name: "Plex", kind: "Plex", description: "Media server", url: "http://server-pc:32400", healthUrl: "http://server-pc:32400/identity", apiKey: "", restartEnabled: false },
      { id: "radarr", name: "Radarr", kind: "Radarr", description: "Movie automation", url: "http://server-pc:7878", healthUrl: "http://server-pc:7878/ping", apiKey: "", restartEnabled: false },
      { id: "sonarr", name: "Sonarr", kind: "Sonarr", description: "TV automation", url: "http://server-pc:8989", healthUrl: "http://server-pc:8989/ping", apiKey: "", restartEnabled: false }
    ],
    newsFeeds: [
      { name: "Ars Technica", url: "https://feeds.arstechnica.com/arstechnica/index" },
      { name: "The Verge", url: "https://www.theverge.com/rss/index.xml" }
    ]
  });

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setSnapshot(await getDashboard());
      setAuthenticated(true);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Dashboard request failed.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void getSetupStatus()
      .then((status) => setSetupStatus(status))
      .catch(() => setSetupStatus(null));

    void getSession()
      .then((session) => {
        setAuthenticated(session.isAuthenticated);
        if (session.isAuthenticated) {
          void load();
        } else {
          setLoading(false);
        }
      })
      .catch(() => setLoading(false));
  }, []);

  useEffect(() => {
    if (!authenticated) {
      return;
    }

    const events = new EventSource(dashboardEventsUrl(), { withCredentials: true });
    events.onmessage = (event) => {
      try {
        setSnapshot(parseDashboardSnapshot(event.data));
        setLiveError(null);
        setLoading(false);
      } catch {
        setLiveError("Live updates paused after a malformed update. Polling is still active.");
        events.close();
      }
    };
    events.onerror = () => {
      setLiveError("Live updates disconnected. Polling is still active.");
      events.close();
    };

    const handle = window.setInterval(() => void load(), 30_000);
    return () => {
      events.close();
      window.clearInterval(handle);
    };
  }, [authenticated]);

  async function signIn(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSigningIn(true);
    setError(null);
    try {
      const session = await login(password);
      setAuthenticated(session.isAuthenticated);
      setPassword("");
      await load();
    } catch {
      setError("Sign in failed. Check the dashboard password in API configuration.");
    } finally {
      setSigningIn(false);
    }
  }

  async function signOut() {
    await logout();
    setAuthenticated(false);
    setSnapshot(null);
  }

  async function restart(serviceId: string) {
    const service = (snapshot?.services ?? []).find((candidate) => candidate.id === serviceId);
    if (!window.confirm(`Restart ${service?.name ?? serviceId}? This can interrupt active users and downloads.`)) {
      return;
    }

    await requestRestart(serviceId);
    await load();
  }

  async function completeSetup(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSetupSaving(true);
    setError(null);
    try {
      setSetupStatus(await saveSetup(setupForm));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Setup failed.");
    } finally {
      setSetupSaving(false);
    }
  }

  if (setupStatus?.usesPlaceholderSecrets) {
    return (
      <main className="auth-shell">
        <form className="setup-panel" onSubmit={(event) => void completeSetup(event)}>
          <div className="login-mark">
            <Wand2 size={28} />
          </div>
          <h1>First-run setup</h1>
          <p>Set the browser password, agent identity, and starter service cards.</p>
          <div className="form-grid">
            <label>
              Dashboard password
              <input type="password" value={setupForm.dashboardPassword} onChange={(event) => setSetupForm({ ...setupForm, dashboardPassword: event.target.value })} required />
            </label>
            <label>
              Default agent ID
              <input value={setupForm.defaultAgentId} onChange={(event) => setSetupForm({ ...setupForm, defaultAgentId: event.target.value })} />
            </label>
            <label>
              Dashboard API key
              <input value={setupForm.dashboardApiKey ?? ""} onChange={(event) => setSetupForm({ ...setupForm, dashboardApiKey: event.target.value })} placeholder="Generated if blank" />
            </label>
            <label>
              Agent API key
              <input value={setupForm.agentApiKey ?? ""} onChange={(event) => setSetupForm({ ...setupForm, agentApiKey: event.target.value })} placeholder="Generated if blank" />
            </label>
          </div>
          <div className="setup-list">
            {setupForm.services.map((service, index) => (
              <div className="setup-service" key={service.id}>
                <input value={service.name} onChange={(event) => updateSetupService(index, { name: event.target.value })} />
                <select value={service.kind} onChange={(event) => updateSetupService(index, { kind: event.target.value as ServiceKind })}>
                  {["Generic", "Plex", "Sonarr", "Radarr", "Prowlarr", "qBittorrent", "SABnzbd", "Jellyfin"].map((kind) => (
                    <option key={kind}>{kind}</option>
                  ))}
                </select>
                <input value={service.url ?? ""} onChange={(event) => updateSetupService(index, { url: event.target.value })} placeholder="URL" />
              </div>
            ))}
          </div>
          <button type="submit" disabled={setupSaving || setupForm.dashboardPassword.length === 0}>
            {setupSaving ? "Saving..." : "Save setup"}
          </button>
          {setupStatus.requiresRestart ? <div className="success-banner"><CheckCircle2 size={18} /> Setup saved. Restart the API to load the new secure values.</div> : null}
          {error ? <div className="error-banner compact">{error}</div> : null}
        </form>
      </main>
    );
  }

  function updateSetupService(index: number, patch: Partial<SetupRequest["services"][number]>) {
    setSetupForm({
      ...setupForm,
      services: setupForm.services.map((service, serviceIndex) => serviceIndex === index ? { ...service, ...patch } : service)
    });
  }

  if (!authenticated) {
    return (
      <main className="auth-shell">
        <form className="login-panel" onSubmit={(event) => void signIn(event)}>
          <div className="login-mark">
            <ShieldCheck size={28} />
          </div>
          <h1>HomeDashboard</h1>
          <p>Sign in to view services and control the remote agent.</p>
          <input
            aria-label="Dashboard password"
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            placeholder="Dashboard password"
            autoFocus
          />
          <button type="submit" disabled={signingIn || password.length === 0}>
            {signingIn ? "Signing in..." : "Sign in"}
          </button>
          {error ? <div className="error-banner compact">{error}</div> : null}
        </form>
      </main>
    );
  }

  const services = snapshot?.services ?? [];
  const agents = snapshot?.agents ?? [];
  const alerts = snapshot?.notifications ?? [];
  const onlineCount = services.filter((service) => service.status === "Online").length;
  const issueCount = services.filter((service) => service.status === "Offline" || service.status === "Degraded").length;
  const lastUpdated = snapshot?.generatedAt ? new Date(snapshot.generatedAt).toLocaleTimeString() : "--";

  return (
    <main className="app-shell">
      <header className="topbar">
        <div>
          <span className="eyebrow">Home operations</span>
          <h1>HomeDashboard</h1>
          <p>Live service health, host telemetry, restart controls, and news.</p>
        </div>
        <div className="topbar-actions">
          <div className="live-pill" title="Dashboard update status">
            <Signal size={16} />
            <span>{liveError ? "Polling" : "Live"}</span>
          </div>
          <button className="refresh-button" type="button" onClick={() => void load()} disabled={loading}>
            <RefreshCw size={18} />
            Refresh
          </button>
          <button className="icon-button" type="button" onClick={() => void signOut()} title="Sign out">
            <LogOut size={18} />
          </button>
        </div>
      </header>

      {error ? <div className="error-banner">{error}</div> : null}
      {liveError ? <div className="warning-banner">{liveError}</div> : null}

      {snapshot ? (
        <>
          <section className="overview-strip" aria-label="Dashboard summary">
            <div className="overview-card strong">
              <div className="overview-icon online">
                <Server size={20} />
              </div>
              <div>
                <span>Online services</span>
                <strong>{onlineCount}/{services.length}</strong>
              </div>
            </div>
            <div className="overview-card">
              <div className={`overview-icon ${issueCount > 0 ? "critical" : "quiet"}`}>
                <TriangleAlert size={20} />
              </div>
              <div>
                <span>Needs attention</span>
                <strong>{issueCount}</strong>
              </div>
            </div>
            <div className="overview-card">
              <div className="overview-icon agent">
                <Activity size={20} />
              </div>
              <div>
                <span>Agents</span>
                <strong>{agents.length}</strong>
              </div>
            </div>
            <div className="overview-card">
              <div className="overview-icon alerts">
                <Bell size={20} />
              </div>
              <div>
                <span>Alerts</span>
                <strong>{alerts.length}</strong>
              </div>
            </div>
            <div className="overview-card timestamp">
              <div className="overview-icon quiet">
                <Clock3 size={20} />
              </div>
              <div>
                <span>Last update</span>
                <strong>{lastUpdated}</strong>
              </div>
            </div>
          </section>

          <div className="dashboard-layout">
            <div className="main-column">
              <ServiceGrid services={services} onRestart={(serviceId) => void restart(serviceId)} />
            </div>
            <aside className="side-column">
              <NotificationPanel notifications={alerts} />
              <section className="panel agents-panel">
                <div className="section-heading">
                  <div>
                    <span className="section-kicker">Remote heartbeat</span>
                    <h2>Agents</h2>
                  </div>
                  <span>{agents.length} connected</span>
                </div>
                <div className="agent-list">
                  {agents.length > 0 ? (
                    agents.map((agent) => (
                      <div className="agent-row" key={agent.agentId}>
                        <span className={`status ${agent.status.toLowerCase()}`}>{agent.status}</span>
                        <div>
                          <strong>{agent.hostname}</strong>
                          <span>{agent.servicesMonitored} services · {new Date(agent.lastSeenAt).toLocaleTimeString()}</span>
                        </div>
                      </div>
                    ))
                  ) : (
                    <div className="empty-state">No agent heartbeat yet.</div>
                  )}
                </div>
              </section>
              <SystemPanel system={snapshot.system} />
              <AuditPanel events={snapshot.recentAuditEvents} />
              <NewsPanel items={snapshot.news} />
            </aside>
          </div>
        </>
      ) : (
        <div className="loading-panel">
          <Activity size={22} />
          <span>{loading ? "Loading dashboard..." : "Dashboard unavailable."}</span>
        </div>
      )}
    </main>
  );
}

function NotificationPanel({ notifications }: { notifications: DashboardNotification[] }) {
  const items = Array.isArray(notifications) ? notifications : [];
  return (
    <section className="panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">Signal check</span>
          <h2>Alerts</h2>
        </div>
        <span>{items.length}</span>
      </div>
      <div className="notice-list">
        {items.length > 0 ? items.map((notification) => (
          <div className={`notice ${notification.severity.toLowerCase()}`} key={notification.id}>
            <Bell size={16} />
            <div>
              <strong>{notification.title}</strong>
              <span>{notification.message}</span>
            </div>
          </div>
        )) : <div className="empty-state">No active alerts.</div>}
      </div>
    </section>
  );
}

function AuditPanel({ events }: { events: AuditEvent[] }) {
  const items = Array.isArray(events) ? events : [];
  return (
    <section className="panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">Recent activity</span>
          <h2>Audit</h2>
        </div>
        <span>{items.length} recent</span>
      </div>
      <div className="audit-list">
        {items.length > 0 ? items.map((event) => (
          <div className="audit-row" key={event.id}>
            <History size={15} />
            <div>
              <strong>{event.type}</strong>
              <span>{event.message}</span>
              <time>{new Date(event.occurredAt).toLocaleString()}</time>
            </div>
          </div>
        )) : <div className="empty-state">No audit events yet.</div>}
      </div>
    </section>
  );
}
