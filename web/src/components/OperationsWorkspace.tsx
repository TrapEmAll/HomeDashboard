import { useEffect, useMemo, useRef, useState } from "react";
import type { ChangeEvent } from "react";
import { Activity, AlertTriangle, BellRing, CalendarDays, CheckCircle2, Clock3, CloudSun, Download, Eye, EyeOff, FileUp, Film, Gauge, GitBranch, HardDrive, History, Laptop, LoaderCircle, Network, Pause, Play, Radar, RefreshCw, RotateCw, Save, Search, ShieldCheck, Siren, Trash2, Wrench } from "lucide-react";
import { controlDownload, createMaintenanceWindow, discoverServices, downloadBackup, getOperations, removeMaintenanceWindow, restoreBackup, runArrCommand } from "../lib/api";
import type { ArrCommandAction, DownloadControlAction, OperationsSnapshot, ServiceDiscoveryResult } from "../types/dashboard";

interface Props {
  authenticated: boolean;
}

type OperationsTab = "activity" | "media" | "downloads" | "reliability" | "maintenance";
type ModuleId = "activity" | "playback" | "calendar" | "downloads" | "incidents" | "storage" | "uptime" | "maintenance" | "toolkit";

const moduleLabels: Record<ModuleId, string> = {
  activity: "Activity",
  playback: "Now playing",
  calendar: "Calendar",
  downloads: "Downloads",
  incidents: "Incidents",
  storage: "Storage",
  uptime: "Uptime",
  maintenance: "Maintenance",
  toolkit: "Toolkit"
};

export function OperationsWorkspace({ authenticated }: Props) {
  const [snapshot, setSnapshot] = useState<OperationsSnapshot | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<OperationsTab>("activity");
  const [clock, setClock] = useState(new Date());
  const [customizing, setCustomizing] = useState(false);
  const [hidden, setHidden] = useState<ModuleId[]>(() => readHiddenModules());
  const [discovery, setDiscovery] = useState<ServiceDiscoveryResult | null>(null);
  const [discovering, setDiscovering] = useState(false);
  const [maintenanceTitle, setMaintenanceTitle] = useState("");
  const [maintenanceStart, setMaintenanceStart] = useState(() => toLocalInput(new Date(Date.now() + 15 * 60_000)));
  const [maintenanceEnd, setMaintenanceEnd] = useState(() => toLocalInput(new Date(Date.now() + 75 * 60_000)));
  const [weather, setWeather] = useState<WeatherState | null>(() => readWeather());
  const [weatherLoading, setWeatherLoading] = useState(false);
  const [arrBusy, setArrBusy] = useState<string | null>(null);
  const notifiedIncidents = useRef(new Set<string>());

  async function load() {
    if (!authenticated) return;
    setLoading(true);
    setError(null);
    try {
      setSnapshot(await getOperations());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Operations data could not be loaded.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    if (!authenticated) return;
    void load();
    const refresh = window.setInterval(() => { if (!document.hidden && navigator.onLine) void load(); }, 60_000);
    const handleVisibility = () => { if (!document.hidden && navigator.onLine) void load(); };
    document.addEventListener("visibilitychange", handleVisibility);
    return () => { window.clearInterval(refresh); document.removeEventListener("visibilitychange", handleVisibility); };
  }, [authenticated]);

  useEffect(() => {
    const timer = window.setInterval(() => setClock(new Date()), 1_000);
    return () => window.clearInterval(timer);
  }, []);

  useEffect(() => {
    if (!snapshot || !("Notification" in window) || Notification.permission !== "granted") return;
    snapshot.incidents.filter((item) => item.severity === "Critical").forEach((item) => {
      if (!notifiedIncidents.current.has(item.id)) {
        notifiedIncidents.current.add(item.id);
        new Notification(`${item.serviceName} needs attention`, { body: item.message, tag: item.id });
      }
    });
  }, [snapshot]);

  const visible = (id: ModuleId) => !hidden.includes(id);

  async function runMediaCommand(serviceId: string, action: ArrCommandAction) {
    const key = `${serviceId}-${action}`;
    setArrBusy(key); setError(null);
    try {
      let result = await runArrCommand(serviceId, action);
      if (result.requiresConfirmation && window.confirm(result.message)) result = await runArrCommand(serviceId, action, true);
      if (!result.succeeded) throw new Error(result.message);
      await load();
    } catch (ex) { setError(ex instanceof Error ? ex.message : "Media command failed."); }
    finally { setArrBusy(null); }
  }
  function toggleModule(id: ModuleId) {
    setHidden((current) => {
      const next = current.includes(id) ? current.filter((item) => item !== id) : [...current, id];
      localStorage.setItem("homedashboard-hidden-modules", JSON.stringify(next));
      return next;
    });
  }

  async function runDownloadAction(source: string, id: string, action: DownloadControlAction) {
    if (action === "Remove" && !window.confirm("Remove this download from the queue? Downloaded data will be kept.")) return;
    try {
      await controlDownload(source, id, action);
      await load();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Download action failed.");
    }
  }

  async function scan() {
    setDiscovering(true);
    setError(null);
    try {
      setDiscovery(await discoverServices());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Service discovery failed.");
    } finally {
      setDiscovering(false);
    }
  }

  async function scheduleMaintenance() {
    if (!maintenanceTitle.trim()) return;
    try {
      await createMaintenanceWindow({
        title: maintenanceTitle,
        startsAt: new Date(maintenanceStart).toISOString(),
        endsAt: new Date(maintenanceEnd).toISOString(),
        serviceId: null,
        suppressAlerts: true
      });
      setMaintenanceTitle("");
      await load();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Maintenance could not be scheduled.");
    }
  }

  async function deleteMaintenance(id: string) {
    if (!window.confirm("Remove this maintenance window?")) return;
    await removeMaintenanceWindow(id);
    await load();
  }

  async function enableAlerts() {
    if (!("Notification" in window)) {
      setError("This browser does not support desktop notifications.");
      return;
    }
    const permission = await Notification.requestPermission();
    if (permission === "granted") {
      new Notification("HomeDashboard alerts enabled", { body: "Critical service incidents can now appear on this device." });
    }
  }

  async function enableWeather() {
    if (!("geolocation" in navigator)) {
      setError("Location is not available in this browser.");
      return;
    }
    setWeatherLoading(true);
    setError(null);
    try {
      const position = await new Promise<GeolocationPosition>((resolve, reject) => navigator.geolocation.getCurrentPosition(resolve, reject, { timeout: 10_000 }));
      const { latitude, longitude } = position.coords;
      const response = await fetch(`https://api.open-meteo.com/v1/forecast?latitude=${latitude.toFixed(3)}&longitude=${longitude.toFixed(3)}&current=temperature_2m,weather_code&temperature_unit=fahrenheit`);
      if (!response.ok) throw new Error("Weather service did not respond.");
      const data = await response.json() as { current?: { temperature_2m?: number; weather_code?: number } };
      const next = { temperature: Math.round(data.current?.temperature_2m ?? 0), condition: weatherCondition(data.current?.weather_code ?? 0), savedAt: Date.now() };
      localStorage.setItem("homedashboard-weather", JSON.stringify(next));
      setWeather(next);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Local weather could not be loaded.");
    } finally {
      setWeatherLoading(false);
    }
  }

  async function restore(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = "";
    if (!file || !window.confirm("Restore dashboard settings and maintenance windows from this backup? Saved API keys will be preserved where service IDs match.")) return;
    try {
      await restoreBackup(await file.text());
      setError("Backup restored. Restart the API to apply its configuration.");
      await load();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Backup restore failed.");
    }
  }

  const tabs: Array<{ id: OperationsTab; label: string; icon: typeof Activity; count?: number }> = [
    { id: "activity", label: "Activity", icon: Activity, count: snapshot?.activity.length },
    { id: "media", label: "Media", icon: Film, count: (snapshot?.playbackSessions.length ?? 0) + (snapshot?.calendar.length ?? 0) },
    { id: "downloads", label: "Downloads", icon: Download, count: snapshot?.downloads.length },
    { id: "reliability", label: "Reliability", icon: Gauge, count: snapshot?.incidents.length },
    { id: "maintenance", label: "Maintenance", icon: Wrench, count: snapshot?.maintenance.length }
  ];

  return (
    <section className="operations-workspace" id="operations">
      <header className="operations-header">
        <div>
          <span className="section-kicker">Operations center</span>
          <h2>Live control room</h2>
        </div>
        <div className="personal-strip">
          <div><Clock3 size={16} /><span>{clock.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</span></div>
          <div><CalendarDays size={16} /><span>{clock.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" })}</span></div>
          <div className={navigator.onLine ? "online" : "offline"}><Network size={16} /><span>{navigator.onLine ? "LAN online" : "Offline"}</span></div>
          <button type="button" title="Load weather using this device's location" onClick={() => void enableWeather()}><CloudSun size={16} /><span>{weatherLoading ? "Loading weather" : weather ? `${weather.temperature}°F · ${weather.condition}` : "Enable weather"}</span></button>
        </div>
        <div className="operations-actions">
          <button className="icon-button" type="button" title="Browser notifications" onClick={() => void enableAlerts()}><BellRing size={17} /></button>
          <button className={`icon-button ${customizing ? "active" : ""}`} type="button" title="Customize modules" onClick={() => setCustomizing((value) => !value)}><Eye size={17} /></button>
          <button className="icon-button" type="button" title="Refresh operations" disabled={loading} onClick={() => void load()}>{loading ? <LoaderCircle className="spin" size={17} /> : <RefreshCw size={17} />}</button>
        </div>
      </header>

      {customizing ? <div className="module-customizer">
        {Object.entries(moduleLabels).map(([id, label]) => <button type="button" className={visible(id as ModuleId) ? "active" : ""} key={id} onClick={() => toggleModule(id as ModuleId)}>
          {visible(id as ModuleId) ? <Eye size={15} /> : <EyeOff size={15} />}{label}
        </button>)}
      </div> : null}

      <nav className="operations-tabs" aria-label="Operations views">
        {tabs.map((item) => <button type="button" className={tab === item.id ? "active" : ""} key={item.id} onClick={() => setTab(item.id)}>
          <item.icon size={16} />{item.label}{typeof item.count === "number" ? <span>{item.count}</span> : null}
        </button>)}
      </nav>

      {error ? <div className="error-banner compact">{error}</div> : null}
      {!snapshot && loading ? <div className="operations-loading"><LoaderCircle className="spin" size={20} /> Loading operational data...</div> : null}
      {snapshot ? <div className="operations-content">
        {tab === "activity" && visible("activity") ? <ActivityView snapshot={snapshot} /> : null}
        {tab === "media" ? <MediaView snapshot={snapshot} showPlayback={visible("playback")} showCalendar={visible("calendar")} busy={arrBusy} onCommand={runMediaCommand} /> : null}
        {tab === "downloads" && visible("downloads") ? <DownloadsView snapshot={snapshot} onAction={runDownloadAction} /> : null}
        {tab === "reliability" ? <ReliabilityView snapshot={snapshot} showIncidents={visible("incidents")} showStorage={visible("storage")} showUptime={visible("uptime")} /> : null}
        {tab === "maintenance" ? <MaintenanceView snapshot={snapshot} showMaintenance={visible("maintenance")} showToolkit={visible("toolkit")} discovery={discovery} discovering={discovering} maintenanceTitle={maintenanceTitle} maintenanceStart={maintenanceStart} maintenanceEnd={maintenanceEnd} onTitle={setMaintenanceTitle} onStart={setMaintenanceStart} onEnd={setMaintenanceEnd} onSchedule={scheduleMaintenance} onRemove={deleteMaintenance} onDiscover={scan} onRestore={restore} /> : null}
      </div> : null}
    </section>
  );
}

function ActivityView({ snapshot }: { snapshot: OperationsSnapshot }) {
  return <div className="operations-grid activity-layout">
    <section className="operation-panel wide"><PanelHeading kicker="Unified timeline" title="Recent activity" meta={`${snapshot.activity.length} events`} />
      <div className="activity-feed">{snapshot.activity.length ? snapshot.activity.map(item => <article className={`activity-entry ${item.severity.toLowerCase()}`} key={item.id}>
        <span className="activity-dot" /><div><strong>{item.title}</strong><p>{item.detail}</p><small>{item.source} · {relativeTime(item.occurredAt)}</small></div><span className="activity-kind">{item.kind}</span>
      </article>) : <Empty label="No recent activity." />}</div>
    </section>
    <section className="operation-panel"><PanelHeading kicker="At a glance" title="Signal summary" />
      <div className="signal-summary"><Metric icon={Film} value={snapshot.playbackSessions.length} label="Streams" /><Metric icon={Download} value={snapshot.downloads.length} label="Downloads" /><Metric icon={Siren} value={snapshot.incidents.length} label="Incidents" /><Metric icon={CalendarDays} value={snapshot.calendar.length} label="Upcoming" /></div>
    </section>
  </div>;
}

function MediaView({ snapshot, showPlayback, showCalendar, busy, onCommand }: { snapshot: OperationsSnapshot; showPlayback: boolean; showCalendar: boolean; busy: string | null; onCommand: (serviceId: string, action: ArrCommandAction) => Promise<void> }) {
  const grouped = useMemo(() => groupCalendar(snapshot.calendar), [snapshot.calendar]);
  const [source, setSource] = useState("all");
  const arr = snapshot.arr ?? { instances: [], queue: [], health: [], history: [] };
  const queue = source === "all" ? arr.queue : arr.queue.filter(item => item.serviceId === source);
  const history = source === "all" ? arr.history : arr.history.filter(item => item.serviceId === source);
  return <div className="media-command-center">
    <section className="operation-panel arr-overview"><PanelHeading kicker="Media automation" title="*arr control deck" meta={`${arr.instances.filter(item => item.connected).length}/${arr.instances.length} connected`} />
      <div className="arr-summary-strip"><span><b>{arr.queue.length}</b> queued</span><span className={arr.health.length ? "warning" : ""}><b>{arr.health.length}</b> issues</span><span><b>{arr.instances.reduce((total, item) => total + item.missingCount, 0)}</b> missing</span><span><b>{arr.history.length}</b> recent events</span></div>
      <div className="arr-instance-list">{arr.instances.length ? arr.instances.map(item => <article key={item.serviceId}>
        <i className={item.connected ? "online" : "offline"} /><div><strong>{item.name}</strong><small>{item.kind}{item.version ? ` · v${item.version}` : ""}</small></div><span><b>{item.queueCount}</b> queue</span><span><b>{item.missingCount}</b> missing</span><span className={item.healthIssueCount ? "warning" : ""}><b>{item.healthIssueCount}</b> issues</span>
        <div className="arr-actions">{item.kind !== "Prowlarr" ? <><button type="button" title="Refresh monitored downloads" disabled={busy !== null} onClick={() => void onCommand(item.serviceId, "RefreshMonitoredDownloads")}>{busy === `${item.serviceId}-RefreshMonitoredDownloads` ? <LoaderCircle className="spin" size={14} /> : <RefreshCw size={14} />}</button><button type="button" title="Search all monitored missing media" disabled={busy !== null} onClick={() => void onCommand(item.serviceId, "SearchMissing")}><Search size={14} /></button></> : null}</div>
      </article>) : <Empty label="Configure an *arr API key to activate media operations." />}</div>
    </section>
    <div className="arr-filter-bar"><strong>Unified feed</strong><button type="button" className={source === "all" ? "active" : ""} onClick={() => setSource("all")}>All apps</button>{arr.instances.map(item => <button type="button" className={source === item.serviceId ? "active" : ""} key={item.serviceId} onClick={() => setSource(item.serviceId)}>{item.name}</button>)}</div>
    <div className="operations-grid arr-detail-layout">
      <section className="operation-panel"><PanelHeading kicker="Action required" title="Health issues" meta={`${arr.health.length} open`} /><div className="arr-health-list">{arr.health.length ? arr.health.map(item => <article key={item.id}><AlertTriangle size={15} /><div><strong>{item.source}</strong><span>{item.message}</span></div><small>{item.type}</small></article>) : <Empty label="No *arr health issues reported." />}</div></section>
      <section className="operation-panel"><PanelHeading kicker="Import pipeline" title="*arr queue" meta={`${queue.length} items`} /><div className="arr-queue-list">{queue.length ? queue.map(item => <article key={`${item.serviceId}-${item.id}`}><div><strong>{item.title}</strong><span>{item.detail ?? item.source}</span><small>{item.source} · {item.trackedStatus ?? item.status}</small></div><b>{item.progressPercent.toFixed(0)}%</b><div className="progress-track"><span style={{ width: `${item.progressPercent}%` }} /></div>{item.errorMessage ? <p>{item.errorMessage}</p> : null}</article>) : <Empty label="The selected *arr queue is clear." />}</div></section>
      <section className="operation-panel wide"><PanelHeading kicker="Library pipeline" title="Recent imports and grabs" meta={`${history.length} events`} /><div className="arr-history-list">{history.length ? history.slice(0, 30).map(item => <article key={`${item.serviceId}-${item.id}`}><History size={14} /><div><strong>{item.title}</strong><span>{item.source}{item.quality ? ` · ${item.quality}` : ""}</span></div><b>{friendlyEvent(item.eventType)}</b><time>{relativeTime(item.occurredAt)}</time></article>) : <Empty label="No recent history for the selected apps." />}</div></section>
    </div>
    <div className="operations-grid media-layout">
    {showPlayback ? <section className="operation-panel"><PanelHeading kicker="Plex sessions" title="Now playing" meta={`${snapshot.playbackSessions.length} active`} />
      <div className="playback-list">{snapshot.playbackSessions.length ? snapshot.playbackSessions.map(session => <article className="playback-row" key={session.id}>
        <div className="playback-art"><Play size={18} /></div><div className="playback-copy"><strong>{session.title}</strong><span>{session.subtitle ?? session.user}</span><small>{session.user} · {session.player} · {session.decision}</small><div className="progress-track"><span style={{ width: `${session.progressPercent}%` }} /></div></div><div className="playback-meta"><b>{session.progressPercent}%</b><span>{session.videoResolution ?? "Video"}</span></div>
      </article>) : <Empty label="Nothing is playing right now." />}</div>
    </section> : null}
    {showCalendar ? <section className="operation-panel wide"><PanelHeading kicker="Sonarr + Radarr" title="Release calendar" meta={`${snapshot.calendar.length} releases`} />
      <div className="calendar-days">{grouped.length ? grouped.map(group => <div className="calendar-day" key={group.date}><header><strong>{group.label}</strong><span>{group.items.length}</span></header>{group.items.map(item => <article key={item.id}><div className={`calendar-type ${item.mediaType.toLowerCase()}`}>{item.mediaType === "Movie" ? <Film size={15} /> : <Laptop size={15} />}</div><div><strong>{item.title}</strong><span>{item.subtitle ?? item.source}</span></div><time>{new Date(item.airsAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</time>{item.hasFile ? <CheckCircle2 size={15} className="success-icon" /> : null}</article>)}</div>) : <Empty label="Add Sonarr or Radarr API keys to load upcoming releases." />}</div>
    </section> : null}
    </div>
  </div>;
}

function DownloadsView({ snapshot, onAction }: { snapshot: OperationsSnapshot; onAction: (source: string, id: string, action: DownloadControlAction) => Promise<void> }) {
  return <section className="operation-panel"><PanelHeading kicker="qBittorrent + SABnzbd" title="Download queues" meta={`${snapshot.downloads.length} active`} />
    <div className="download-table"><div className="download-head"><span>Name</span><span>Status</span><span>Progress</span><span>Speed</span><span>Controls</span></div>
      {snapshot.downloads.length ? snapshot.downloads.map(item => <article className="download-row" key={`${item.source}-${item.id}`}><div><strong>{item.name}</strong><small>{item.source}</small></div><span className={`queue-status ${item.status.toLowerCase()}`}>{item.status}</span><div><b>{item.progressPercent.toFixed(1)}%</b><div className="progress-track"><span style={{ width: `${item.progressPercent}%` }} /></div></div><span>{formatRate(item.downloadSpeedBytes)}</span><div className="queue-actions">{item.canPause ? <><button type="button" title="Pause" onClick={() => void onAction(item.source, item.id, "Pause")}><Pause size={15} /></button><button type="button" title="Resume" onClick={() => void onAction(item.source, item.id, "Resume")}><Play size={15} /></button><button type="button" title="Recheck" onClick={() => void onAction(item.source, item.id, "Recheck")}><RotateCw size={15} /></button></> : null}{item.canRemove ? <button type="button" title="Remove" onClick={() => void onAction(item.source, item.id, "Remove")}><Trash2 size={15} /></button> : null}</div></article>) : <Empty label="No downloads are active, or client authentication is required." />}
    </div>
  </section>;
}

function ReliabilityView({ snapshot, showIncidents, showStorage, showUptime }: { snapshot: OperationsSnapshot; showIncidents: boolean; showStorage: boolean; showUptime: boolean }) {
  return <div className="operations-grid reliability-layout">
    {showIncidents ? <section className="operation-panel"><PanelHeading kicker="Incident tracking" title="Active incidents" meta={`${snapshot.incidents.length} open`} /><div className="incident-list">{snapshot.incidents.length ? snapshot.incidents.map(item => <article className={item.severity.toLowerCase()} key={item.id}><Siren size={17} /><div><strong>{item.serviceName}</strong><span>{item.message}</span><small>Started {relativeTime(item.startedAt)}</small></div></article>) : <Empty label="No active incidents." />}</div></section> : null}
    {showUptime ? <section className="operation-panel"><PanelHeading kicker="Seven-day view" title="Service uptime" /><div className="uptime-list">{snapshot.uptime.map(item => <div key={item.serviceId}><span className={`status-dot ${item.currentStatus.toLowerCase()}`} /><strong>{item.name}</strong><div className="uptime-bar"><span style={{ width: `${item.uptimePercent}%` }} /></div><b>{item.uptimePercent.toFixed(2)}%</b></div>)}</div></section> : null}
    {showUptime ? <section className="operation-panel"><PanelHeading kicker="Root-cause context" title="Service dependencies" /><DependencyMap snapshot={snapshot} /></section> : null}
    {showStorage ? <section className="operation-panel wide"><PanelHeading kicker="Capacity planning" title="Storage intelligence" /><div className="storage-grid">{snapshot.storage.map(item => <article key={item.name}><HardDrive size={20} /><div><strong>{item.name}</strong><span>{formatBytes(item.freeBytes)} free of {formatBytes(item.totalBytes)}</span><div className="storage-track"><span className={item.usedPercent > 90 ? "critical" : ""} style={{ width: `${item.usedPercent}%` }} /></div><small>{item.usedPercent}% used · {item.daysRemaining ? `${item.daysRemaining} estimated days remain` : "Growth baseline collecting"}</small></div></article>)}</div></section> : null}
  </div>;
}

interface MaintenanceProps {
  snapshot: OperationsSnapshot; showMaintenance: boolean; showToolkit: boolean; discovery: ServiceDiscoveryResult | null; discovering: boolean;
  maintenanceTitle: string; maintenanceStart: string; maintenanceEnd: string; onTitle: (value: string) => void; onStart: (value: string) => void; onEnd: (value: string) => void;
  onSchedule: () => Promise<void>; onRemove: (id: string) => Promise<void>; onDiscover: () => Promise<void>;
  onRestore: (event: ChangeEvent<HTMLInputElement>) => Promise<void>;
}

function MaintenanceView(props: MaintenanceProps) {
  return <div className="operations-grid maintenance-layout">
    {props.showMaintenance ? <section className="operation-panel wide"><PanelHeading kicker="Change control" title="Maintenance windows" meta={`${props.snapshot.maintenance.length} scheduled`} /><div className="maintenance-form"><input placeholder="Maintenance title" value={props.maintenanceTitle} onChange={event => props.onTitle(event.target.value)} /><input type="datetime-local" value={props.maintenanceStart} onChange={event => props.onStart(event.target.value)} /><input type="datetime-local" value={props.maintenanceEnd} onChange={event => props.onEnd(event.target.value)} /><button type="button" onClick={() => void props.onSchedule()}><CalendarDays size={16} /> Schedule</button></div><div className="maintenance-list">{props.snapshot.maintenance.length ? props.snapshot.maintenance.map(item => <article key={item.id}><Wrench size={17} /><div><strong>{item.title}</strong><span>{new Date(item.startsAt).toLocaleString()} to {new Date(item.endsAt).toLocaleString()}</span><small>{item.suppressAlerts ? "Alerts suppressed" : "Alerts remain active"}</small></div><button type="button" title="Remove maintenance" onClick={() => void props.onRemove(item.id)}><Trash2 size={15} /></button></article>) : <Empty label="No maintenance is scheduled." />}</div></section> : null}
    {props.showToolkit ? <section className="operation-panel"><PanelHeading kicker="Server toolkit" title="Discovery and backup" /><div className="toolkit-actions"><button type="button" disabled={props.discovering} onClick={() => void props.onDiscover()}><Radar size={18} /><span><strong>Discover services</strong><small>Scan known ports on this server</small></span></button><button type="button" onClick={() => void downloadBackup()}><Save size={18} /><span><strong>Export backup</strong><small>Settings without secret values</small></span></button><label><FileUp size={18} /><span><strong>Restore backup</strong><small>Preserve matching saved credentials</small></span><input type="file" accept="application/json,.json" onChange={event => void props.onRestore(event)} /></label><a href={props.snapshot.update.repositoryUrl} target="_blank" rel="noreferrer"><RefreshCw size={18} /><span><strong>Update center</strong><small>Version {props.snapshot.update.currentVersion} · {props.snapshot.update.channel}</small></span></a><button type="button"><ShieldCheck size={18} /><span><strong>Security status</strong><small>Login throttling and authenticated controls active</small></span></button></div>{props.discovery ? <div className="discovery-results"><strong>{props.discovery.services.length} services found</strong>{props.discovery.services.map(item => <span key={item.id}><i className={item.alreadyConfigured ? "configured" : "new"} />{item.name}<small>:{item.port} · {item.alreadyConfigured ? "configured" : "available"}</small></span>)}</div> : null}</section> : null}
  </div>;
}

function PanelHeading({ kicker, title, meta }: { kicker: string; title: string; meta?: string }) { return <header className="operation-panel-heading"><div><span>{kicker}</span><h3>{title}</h3></div>{meta ? <b>{meta}</b> : null}</header>; }
function Empty({ label }: { label: string }) { return <div className="operation-empty"><History size={18} /><span>{label}</span></div>; }
function Metric({ icon: Icon, value, label }: { icon: typeof Activity; value: number; label: string }) { return <div><Icon size={18} /><strong>{value}</strong><span>{label}</span></div>; }
function DependencyMap({ snapshot }: { snapshot: OperationsSnapshot }) {
  const find = (terms: string[]) => snapshot.uptime.find(item => terms.some(term => `${item.serviceId} ${item.name}`.toLowerCase().includes(term)));
  const dependencies = [
    { name: "Media requests", nodes: [find(["sonarr"]), find(["radarr"]), find(["prowlarr"]), find(["qbittorrent", "sabnzbd"])] },
    { name: "Playback", nodes: [find(["plex", "jellyfin"])] }
  ].filter(group => group.nodes.some(Boolean));
  return <div className="dependency-map">{dependencies.length ? dependencies.map(group => <article key={group.name}><GitBranch size={17} /><strong>{group.name}</strong><div>{group.nodes.filter(Boolean).map(node => <span key={node!.serviceId} className={node!.currentStatus.toLowerCase()}>{node!.name}</span>)}</div></article>) : <Empty label="Configure media services to map their dependencies." />}</div>;
}
interface WeatherState { temperature: number; condition: string; savedAt: number }
function readWeather(): WeatherState | null { try { const value = JSON.parse(localStorage.getItem("homedashboard-weather") ?? "null") as WeatherState | null; return value && Date.now() - value.savedAt < 3_600_000 ? value : null; } catch { return null; } }
function weatherCondition(code: number) { if (code === 0) return "Clear"; if (code <= 3) return "Cloudy"; if (code <= 48) return "Fog"; if (code <= 67) return "Rain"; if (code <= 77) return "Snow"; if (code <= 82) return "Showers"; return "Storms"; }
function readHiddenModules(): ModuleId[] { try { const parsed = JSON.parse(localStorage.getItem("homedashboard-hidden-modules") ?? "[]"); return Array.isArray(parsed) ? parsed : []; } catch { return []; } }
function relativeTime(value: string) { const seconds = Math.max(0, Math.round((Date.now() - new Date(value).getTime()) / 1000)); if (seconds < 60) return `${seconds}s ago`; if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`; if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`; return `${Math.floor(seconds / 86400)}d ago`; }
function friendlyEvent(value: string) { return value.replace(/([a-z])([A-Z])/g, "$1 $2").replace(/_/g, " ").toLowerCase(); }
function formatBytes(value?: number | null) { if (!value) return "0 B"; const units = ["B", "KB", "MB", "GB", "TB"]; const index = Math.min(Math.floor(Math.log(value) / Math.log(1024)), units.length - 1); return `${(value / 1024 ** index).toFixed(index > 2 ? 1 : 0)} ${units[index]}`; }
function formatRate(value?: number | null) { return value ? `${formatBytes(value)}/s` : "Idle"; }
function toLocalInput(value: Date) { const offset = value.getTimezoneOffset() * 60_000; return new Date(value.getTime() - offset).toISOString().slice(0, 16); }
function groupCalendar(items: OperationsSnapshot["calendar"]) { const groups = new Map<string, typeof items>(); items.forEach(item => { const date = item.airsAt.slice(0, 10); groups.set(date, [...(groups.get(date) ?? []), item]); }); return [...groups.entries()].map(([date, groupedItems]) => ({ date, label: new Date(`${date}T12:00:00`).toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" }), items: groupedItems })).slice(0, 14); }
