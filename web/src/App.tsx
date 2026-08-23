import { useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent } from "react";
import { Activity, Bell, CheckCircle2, Clock3, Columns3, Gauge, History, LayoutGrid, LogOut, PanelRightClose, PanelRightOpen, RefreshCw, Search, Server, Settings, ShieldCheck, Signal, TriangleAlert, Wand2, X } from "lucide-react";
import { ApiError, dashboardEventsUrl, getAgentHistory, getDashboard, getDashboardSettings, getSession, getSetupStatus, login, logout, parseDashboardSnapshot, requestRestart, saveSetup, updateDashboardSettings } from "./lib/api";
import { NewsPanel } from "./components/NewsPanel";
import { OperationsWorkspace } from "./components/OperationsWorkspace";
import { ServiceGrid } from "./components/ServiceGrid";
import { SettingsDrawer } from "./components/SettingsDrawer";
import { SystemPanel } from "./components/SystemPanel";
import type { AgentHistoryPoint, AuditEvent, DashboardNotification, DashboardSettings, DashboardSnapshot, ServiceKind, ServiceStatus, SetupRequest, SetupStatus, UpdateDashboardSettingsRequest } from "./types/dashboard";
import "./styles.css";

export function App() {
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [liveError, setLiveError] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [authenticated, setAuthenticated] = useState(false);
  const [password, setPassword] = useState("");
  const [signingIn, setSigningIn] = useState(false);
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null);
  const [setupSaving, setSetupSaving] = useState(false);
  const [settings, setSettings] = useState<DashboardSettings | null>(null);
  const [settingsLoading, setSettingsLoading] = useState(false);
  const [settingsSaving, setSettingsSaving] = useState(false);
  const [settingsError, setSettingsError] = useState<string | null>(null);
  const [agentHistory, setAgentHistory] = useState<AgentHistoryPoint[]>([]);
  const [query, setQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState<"All" | ServiceStatus>("All");
  const [density, setDensity] = useState<"compact" | "comfortable">(() => localStorage.getItem("homedashboard-density") === "comfortable" ? "comfortable" : "compact");
  const [showSidebar, setShowSidebar] = useState(() => localStorage.getItem("homedashboard-sidebar") !== "hidden");
  const [favorites, setFavorites] = useState<string[]>(() => readStoredArray("homedashboard-favorites"));
  const [healthHistory, setHealthHistory] = useState<number[]>(() => readStoredNumbers("homedashboard-health-history"));
  const searchRef = useRef<HTMLInputElement>(null);
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
      if (ex instanceof ApiError && ex.status === 401) {
        setAuthenticated(false);
        setSnapshot(null);
        setError("Your session expired. Sign in again.");
        return;
      }
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

  const primaryAgentId = snapshot?.agents[0]?.agentId ?? setupStatus?.defaultAgentId;
  useEffect(() => {
    if (!authenticated || !primaryAgentId) {
      setAgentHistory([]);
      return;
    }

    void getAgentHistory(primaryAgentId)
      .then(setAgentHistory)
      .catch(() => setAgentHistory([]));
  }, [authenticated, primaryAgentId, snapshot?.generatedAt]);

  useEffect(() => {
    function handleKeyboard(event: KeyboardEvent) {
      const target = event.target as HTMLElement | null;
      const isEditing = target?.tagName === "INPUT" || target?.tagName === "SELECT" || target?.tagName === "TEXTAREA";
      if (event.key === "Escape" && settings) {
        setSettings(null);
        return;
      }
      if (isEditing || event.ctrlKey || event.metaKey || event.altKey) {
        return;
      }
      if (event.key === "/") {
        event.preventDefault();
        searchRef.current?.focus();
      } else if (event.key.toLowerCase() === "r") {
        event.preventDefault();
        void load();
      } else if (event.key.toLowerCase() === "s") {
        event.preventDefault();
        void openSettings();
      }
    }

    window.addEventListener("keydown", handleKeyboard);
    return () => window.removeEventListener("keydown", handleKeyboard);
  }, [settings]);

  useEffect(() => {
    if (!snapshot || snapshot.services.length === 0) {
      return;
    }

    const score = Math.round(snapshot.services.reduce((total, service) => total + (service.status === "Online" ? 100 : service.status === "Degraded" ? 50 : 0), 0) / snapshot.services.length);
    setHealthHistory((current) => {
      const next = [...current, score].slice(-24);
      localStorage.setItem("homedashboard-health-history", JSON.stringify(next));
      return next;
    });
  }, [snapshot?.generatedAt]);

  const services = snapshot?.services ?? [];
  const filteredServices = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return services.filter((service) => {
      const matchesStatus = statusFilter === "All" || service.status === statusFilter;
      const matchesQuery = needle.length === 0 || [service.name, service.kind, service.description, service.statusMessage]
        .some((value) => value?.toLocaleLowerCase().includes(needle));
      return matchesStatus && matchesQuery;
    });
  }, [query, services, statusFilter]);

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

    setError(null);
    setActionMessage(null);
    try {
      await requestRestart(serviceId);
      setActionMessage(`${service?.name ?? serviceId} restart queued.`);
      window.setTimeout(() => setActionMessage(null), 5000);
      await load();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Restart request failed.");
    }
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

  async function openSettings() {
    if (settingsLoading) {
      return;
    }
    setSettingsLoading(true);
    setSettingsError(null);
    try {
      setSettings(await getDashboardSettings());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Settings could not be loaded.");
    } finally {
      setSettingsLoading(false);
    }
  }

  async function saveSettings(request: UpdateDashboardSettingsRequest) {
    setSettingsSaving(true);
    setSettingsError(null);
    try {
      const saved = await updateDashboardSettings(request);
      setSettings(saved);
      setActionMessage("Settings saved. Restart the API when convenient to apply them.");
    } catch (ex) {
      setSettingsError(ex instanceof Error ? ex.message : "Settings could not be saved.");
    } finally {
      setSettingsSaving(false);
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

  const agents = snapshot?.agents ?? [];
  const alerts = snapshot?.notifications ?? [];
  const onlineCount = services.filter((service) => service.status === "Online").length;
  const issueCount = services.filter((service) => service.status === "Offline" || service.status === "Degraded").length;
  const lastUpdated = snapshot?.generatedAt ? new Date(snapshot.generatedAt).toLocaleTimeString() : "--";
  function toggleFavorite(serviceId: string) {
    setFavorites((current) => {
      const next = current.includes(serviceId) ? current.filter((id) => id !== serviceId) : [...current, serviceId];
      localStorage.setItem("homedashboard-favorites", JSON.stringify(next));
      return next;
    });
  }

  function toggleDensity() {
    const next = density === "compact" ? "comfortable" : "compact";
    setDensity(next);
    localStorage.setItem("homedashboard-density", next);
  }

  function toggleSidebar() {
    const next = !showSidebar;
    setShowSidebar(next);
    localStorage.setItem("homedashboard-sidebar", next ? "visible" : "hidden");
  }

  return (
    <main className="app-shell" data-density={density}>
      <header className="topbar">
        <div className="brand-lockup">
          <div className="brand-mark"><Gauge size={22} /></div>
          <div>
            <span className="eyebrow">Home operations</span>
            <h1>HomeDashboard</h1>
          </div>
        </div>
        <nav className="topbar-nav" aria-label="Dashboard sections">
          <button type="button" onClick={() => document.getElementById("overview")?.scrollIntoView({ behavior: "smooth" })}>Overview</button>
          <button type="button" onClick={() => document.getElementById("services")?.scrollIntoView({ behavior: "smooth" })}>Services</button>
          <button type="button" onClick={() => document.getElementById("activity")?.scrollIntoView({ behavior: "smooth" })}>Activity</button>
          <button type="button" onClick={() => document.getElementById("operations")?.scrollIntoView({ behavior: "smooth" })}>Operations</button>
          <button type="button" onClick={() => document.getElementById("content")?.scrollIntoView({ behavior: "smooth" })}>Intelligence</button>
        </nav>
        <div className="topbar-actions">
          <div className="live-pill" title="Dashboard update status">
            <Signal size={16} />
            <span>{liveError ? "Polling" : "Live"}</span>
          </div>
          <button className="icon-button" type="button" onClick={toggleDensity} title={`Use ${density === "compact" ? "comfortable" : "compact"} density`}>
            {density === "compact" ? <LayoutGrid size={18} /> : <Columns3 size={18} />}
          </button>
          <button className="icon-button" type="button" onClick={toggleSidebar} title={showSidebar ? "Hide details" : "Show details"}>
            {showSidebar ? <PanelRightClose size={18} /> : <PanelRightOpen size={18} />}
          </button>
          <button className="icon-button" type="button" onClick={() => void openSettings()} disabled={settingsLoading} title="Dashboard settings (S)">
            <Settings size={18} />
          </button>
          <button className="refresh-button" type="button" onClick={() => void load()} disabled={loading}>
            <RefreshCw size={18} />
            <span>Refresh</span>
          </button>
          <button className="icon-button" type="button" onClick={() => void signOut()} title="Sign out">
            <LogOut size={18} />
          </button>
        </div>
      </header>

      {error ? <div className="error-banner">{error}</div> : null}
      {liveError ? <div className="warning-banner">{liveError}</div> : null}
      {actionMessage ? <div className="success-banner action-banner"><CheckCircle2 size={17} />{actionMessage}</div> : null}

      {snapshot ? (
        <>
          <section className="overview-strip" id="overview" aria-label="Dashboard summary">
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
            <div className="overview-card health-card">
              <div>
                <span>Health pulse</span>
                <strong>{healthHistory.at(-1) ?? 0}%</strong>
              </div>
              <HealthPulse values={healthHistory} />
            </div>
          </section>

          <section className="command-bar" aria-label="Service controls">
            <label className="service-search">
              <Search size={17} />
              <input ref={searchRef} value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search services, types, or status" />
              {query ? <button type="button" onClick={() => setQuery("")} title="Clear search"><X size={16} /></button> : null}
            </label>
            <div className="filter-control" role="group" aria-label="Filter by status">
              {(["All", "Online", "Degraded", "Offline"] as const).map((status) => (
                <button className={statusFilter === status ? "active" : ""} type="button" key={status} onClick={() => setStatusFilter(status)}>
                  {status}<span>{status === "All" ? services.length : services.filter((service) => service.status === status).length}</span>
                </button>
              ))}
            </div>
            <span className="result-count">{filteredServices.length} shown</span>
          </section>

          <div className={`dashboard-layout ${showSidebar ? "" : "sidebar-hidden"}`}>
            <div className="main-column" id="services">
              <ServiceGrid services={filteredServices} favorites={favorites} onToggleFavorite={toggleFavorite} onRestart={(serviceId) => void restart(serviceId)} />
            </div>
            {showSidebar ? <aside className="side-column" id="activity">
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
              <SystemPanel system={snapshot.system} history={agentHistory} />
              <AuditPanel events={snapshot.recentAuditEvents} />
            </aside> : null}
          </div>
          <OperationsWorkspace authenticated={authenticated} />
          <NewsPanel items={snapshot.news} />
        </>
      ) : (
        <div className="loading-panel">
          <Activity size={22} />
          <span>{loading ? "Loading dashboard..." : "Dashboard unavailable."}</span>
        </div>
      )}
      {settings ? <SettingsDrawer settings={settings} saving={settingsSaving} error={settingsError} onClose={() => setSettings(null)} onSave={saveSettings} /> : null}
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

function HealthPulse({ values }: { values: number[] }) {
  const points = values.length > 0 ? values : [0];
  return (
    <div className="health-pulse" aria-label={`Recent health ${points[points.length - 1]} percent`}>
      {points.map((value, index) => (
        <span key={`${index}-${value}`} style={{ height: `${Math.max(12, value)}%` }} />
      ))}
    </div>
  );
}

function readStoredArray(key: string): string[] {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? "[]");
    return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
  } catch {
    return [];
  }
}

function readStoredNumbers(key: string): number[] {
  try {
    const value = JSON.parse(localStorage.getItem(key) ?? "[]");
    return Array.isArray(value) ? value.filter((item): item is number => typeof item === "number" && Number.isFinite(item)).slice(-24) : [];
  } catch {
    return [];
  }
}
