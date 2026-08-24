import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Activity, AlarmClock, Archive, Bell, Bot, Box, CalendarDays, Camera, Check, ChevronRight, CircleGauge,
  CloudLightning, Command, Cpu, ExternalLink, FileText, Gamepad2, Github, Home, Inbox, Lightbulb, ListChecks,
  Maximize2, Mic, Network, NotebookPen, Package, Play, Plus, Power, Radio, RefreshCw, Search, Send, Server,
  Settings2, ShieldCheck, ShoppingCart, Sparkles, SquareStack, Trash2, Users, WandSparkles, X, Zap
} from "lucide-react";
import {
  askAssistant, browseCommandCenterFiles, deleteCommandCenterItem, getCommandCenter, getCommandCenterLogs,
  runCommandCenterAction, saveCommandCenterItem, searchCommandCenter, updateCommandCenterIntegration
} from "../lib/api";
import type {
  AssistantResponse, CommandCenterActionRequest, CommandCenterItemRequest, CommandCenterSnapshot,
  FileWorkspaceEntry, IntegrationStatus, SearchResult, SystemLogEntry
} from "../types/commandCenter";

type Tab = "today" | "planner" | "home" | "systems" | "automations" | "integrations";
type CaptureKind = "task" | "calendar" | "note" | "shopping" | "package" | "media" | "automation" | "asset" | "profile";
const tabs: Array<{ id: Tab; label: string; icon: typeof Home }> = [
  { id: "today", label: "Today", icon: Sparkles },
  { id: "planner", label: "Plan", icon: CalendarDays },
  { id: "home", label: "Home", icon: Home },
  { id: "systems", label: "Systems", icon: Server },
  { id: "automations", label: "Automate", icon: WandSparkles },
  { id: "integrations", label: "Connect", icon: SquareStack }
];
const modes = ["Home", "Away", "Sleep", "Work", "Gaming", "Movie", "Guest"];
const defaultWidgets = ["briefing", "agenda", "tasks", "inbox", "deliveries", "shopping", "media", "notes", "activity"];

export function CommandCenter({ authenticated }: { authenticated: boolean }) {
  const [snapshot, setSnapshot] = useState<CommandCenterSnapshot | null>(null);
  const [tab, setTab] = useState<Tab>("today");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [capture, setCapture] = useState<CaptureKind | null>(null);
  const [palette, setPalette] = useState(false);
  const [integration, setIntegration] = useState<IntegrationStatus | null>(null);
  const [assistant, setAssistant] = useState<AssistantResponse | null>(null);
  const [assistantInput, setAssistantInput] = useState("");
  const [wallMode, setWallMode] = useState(() => localStorage.getItem("homedashboard-wall-mode") === "true");
  const [visibleWidgets, setVisibleWidgets] = useState<string[]>(() => readStoredArray("homedashboard-command-widgets", defaultWidgets));
  const [customizing, setCustomizing] = useState(false);
  const [workspace, setWorkspace] = useState<"files" | "logs" | null>(null);

  const load = useCallback(async () => {
    if (!authenticated) return;
    try { setSnapshot(await getCommandCenter()); setError(null); }
    catch (ex) { setError(ex instanceof Error ? ex.message : "Command center could not be loaded."); }
  }, [authenticated]);

  useEffect(() => { void load(); }, [load]);
  useEffect(() => {
    const handle = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") { event.preventDefault(); setPalette(true); }
    };
    window.addEventListener("keydown", handle);
    return () => window.removeEventListener("keydown", handle);
  }, []);
  useEffect(() => {
    if (!authenticated) return;
    const handle = window.setInterval(() => { if (!document.hidden && navigator.onLine) void load(); }, 90_000);
    return () => window.clearInterval(handle);
  }, [authenticated, load]);

  async function act(request: CommandCenterActionRequest) {
    setBusy(true);
    try {
      let result = await runCommandCenterAction(request);
      if (result.requiresConfirmation && window.confirm(result.message)) result = await runCommandCenterAction({ ...request, confirmed: true });
      if (!result.succeeded) throw new Error(result.message);
      await load();
    } catch (ex) { setError(ex instanceof Error ? ex.message : "Action failed."); }
    finally { setBusy(false); }
  }

  async function saveItem(request: CommandCenterItemRequest) {
    setBusy(true);
    try { setSnapshot(await saveCommandCenterItem(request)); setCapture(null); setError(null); }
    catch (ex) { setError(ex instanceof Error ? ex.message : "Item could not be saved."); }
    finally { setBusy(false); }
  }

  async function remove(kind: string, id: string) {
    if (!window.confirm("Remove this item?")) return;
    await deleteCommandCenterItem(kind, id); await load();
  }

  async function ask(prompt = assistantInput) {
    if (!prompt.trim()) return;
    setBusy(true); setAssistantInput(prompt);
    try { setAssistant(await askAssistant(prompt)); setError(null); }
    catch (ex) { setError(ex instanceof Error ? ex.message : "Assistant is unavailable."); }
    finally { setBusy(false); }
  }

  function toggleWallMode() {
    const next = !wallMode; setWallMode(next); localStorage.setItem("homedashboard-wall-mode", String(next));
    if (next) void document.documentElement.requestFullscreen?.();
  }

  function toggleWidget(widget: string) {
    const next = visibleWidgets.includes(widget) ? visibleWidgets.filter((item) => item !== widget) : [...visibleWidgets, widget];
    setVisibleWidgets(next); localStorage.setItem("homedashboard-command-widgets", JSON.stringify(next));
  }

  if (!snapshot) return <section className="command-center-shell" id="command-center"><div className="command-loading"><Bot size={22} /><span>{error ?? "Preparing command center..."}</span><button type="button" onClick={() => void load()}><RefreshCw size={15} />Retry</button></div></section>;
  const openTasks = snapshot.tasks.filter((item) => !item.completed);
  const unread = snapshot.inbox.filter((item) => !item.acknowledged);
  const connected = snapshot.integrations.filter((item) => item.connected);

  return <section className={`command-center-shell ${wallMode ? "wall-mode" : ""}`} id="command-center">
    <header className="command-center-header">
      <div><span className="section-kicker">Personal operations</span><h2>Command center</h2></div>
      <div className="command-center-status"><span><i className={navigator.onLine ? "online" : "offline"} />{snapshot.activeMode}</span><span>{connected.length} connected</span><span>{unread.length} unread</span></div>
      <div className="command-center-tools">
        <button type="button" title="Search everything" onClick={() => setPalette(true)}><Search size={16} /><kbd>Ctrl K</kbd></button>
        <button className={customizing ? "active" : ""} type="button" title="Customize workspace" onClick={() => setCustomizing((value) => !value)}><Settings2 size={16} /></button>
        <button className={wallMode ? "active" : ""} type="button" title="Wall display" onClick={toggleWallMode}><Maximize2 size={16} /></button>
        <button type="button" title="Refresh command center" onClick={() => void load()}><RefreshCw className={busy ? "spin" : ""} size={16} /></button>
      </div>
    </header>

    {error ? <div className="command-error"><CloudLightning size={16} />{error}<button type="button" onClick={() => setError(null)}><X size={14} /></button></div> : null}
    {customizing ? <div className="widget-customizer"><strong>Today workspace</strong>{defaultWidgets.map((widget) => <label key={widget}><input type="checkbox" checked={visibleWidgets.includes(widget)} onChange={() => toggleWidget(widget)} />{widget}</label>)}</div> : null}

    <div className="assistant-console">
      <div className="assistant-avatar"><Bot size={24} /></div>
      <div className="assistant-copy"><strong>{snapshot.briefing.greeting}</strong><span>{assistant?.message ?? snapshot.briefing.summary}</span></div>
      <form onSubmit={(event) => { event.preventDefault(); void ask(); }}><input value={assistantInput} onChange={(event) => setAssistantInput(event.target.value)} placeholder="Ask about your day, home, media, or systems" /><VoiceButton onResult={(value) => { setAssistantInput(value); void ask(value); }} /><button type="submit" disabled={busy || !assistantInput.trim()} title="Ask assistant"><Send size={16} /></button></form>
      {assistant?.proposedActions.map((action) => <button className="assistant-proposal" type="button" key={`${action.tool}-${action.target}`} onClick={() => void act(action)}><Zap size={14} />Approve {friendlyTool(action.tool)}</button>)}
    </div>

    <div className="mode-strip" role="group" aria-label="Household mode">{modes.map((mode) => <button className={snapshot.activeMode === mode ? "active" : ""} type="button" aria-label={mode} key={mode} onClick={() => void act({ tool: "mode.set", target: mode, confirmed: true })}>{modeIcon(mode)}<span>{mode}</span></button>)}</div>

    <div className="command-metrics">
      <Metric icon={ListChecks} value={openTasks.length} label="Open tasks" />
      <Metric icon={CalendarDays} value={snapshot.calendar.filter(today).length} label="Today" />
      <Metric icon={Bell} value={snapshot.briefing.attentionCount} label="Attention" alert={snapshot.briefing.attentionCount > 0} />
      <Metric icon={Package} value={snapshot.packages.length} label="Deliveries" />
      <Metric icon={Home} value={snapshot.homeEntities.length} label="Home entities" />
      <Metric icon={Network} value={snapshot.assets.length} label="Tracked assets" />
    </div>

    <nav className="command-tabs" aria-label="Command center views">{tabs.map((item) => <button className={tab === item.id ? "active" : ""} type="button" aria-label={item.label} key={item.id} onClick={() => setTab(item.id)}><item.icon size={16} /><span>{item.label}</span></button>)}</nav>

    {tab === "today" ? <TodayView snapshot={snapshot} widgets={visibleWidgets} onAction={act} onCapture={setCapture} onDelete={remove} /> : null}
    {tab === "planner" ? <PlannerView snapshot={snapshot} onAction={act} onCapture={setCapture} onDelete={remove} /> : null}
    {tab === "home" ? <HomeView snapshot={snapshot} onAction={act} /> : null}
    {tab === "systems" ? <SystemsView snapshot={snapshot} onCapture={setCapture} onAction={act} /> : null}
    {tab === "systems" ? <SystemWorkspaceBar onOpen={setWorkspace} /> : null}
    {tab === "systems" ? <MachineActionBar onAction={act} /> : null}
    {tab === "automations" ? <AutomationView snapshot={snapshot} onCapture={setCapture} onAction={act} onDelete={remove} /> : null}
    {tab === "integrations" ? <IntegrationView snapshot={snapshot} onConfigure={setIntegration} /> : null}

    {capture ? <CaptureDialog kind={capture} busy={busy} onClose={() => setCapture(null)} onSave={saveItem} /> : null}
    {palette ? <CommandPalette snapshot={snapshot} onClose={() => setPalette(false)} onTab={(next) => { setTab(next); setPalette(false); }} onAsk={(value) => { setPalette(false); void ask(value); }} onCapture={(kind) => { setPalette(false); setCapture(kind); }} /> : null}
    {integration ? <IntegrationDialog integration={integration} busy={busy} onClose={() => setIntegration(null)} onSave={async (request) => { setBusy(true); try { await updateCommandCenterIntegration(integration.id, request); setIntegration(null); await load(); } catch (ex) { setError(ex instanceof Error ? ex.message : "Integration could not be saved."); } finally { setBusy(false); } }} /> : null}
    {workspace ? <WorkspaceDialog kind={workspace} onClose={() => setWorkspace(null)} /> : null}
  </section>;
}

function TodayView({ snapshot, widgets, onAction, onCapture, onDelete }: ViewProps & { widgets: string[] }) {
  return <div className="command-grid today-grid">
    {widgets.includes("briefing") ? <Panel className="briefing-panel" title="Daily briefing" icon={Sparkles} action={<button type="button" onClick={() => onCapture("calendar")}><Plus size={14} />Event</button>}><p>{snapshot.briefing.summary}</p><ul>{snapshot.briefing.highlights.map((item) => <li key={item}>{item}</li>)}</ul></Panel> : null}
    {widgets.includes("agenda") ? <Panel title="Agenda" icon={CalendarDays} meta={`${snapshot.calendar.filter(today).length} today`}>{snapshot.calendar.length ? <div className="agenda-list">{snapshot.calendar.slice(0, 8).map((item) => <article key={item.id}><time>{item.allDay ? "All day" : new Date(item.startsAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</time><div><strong>{item.title}</strong><span>{item.location ?? item.calendar}</span></div><button type="button" title="Remove event" onClick={() => void onDelete("calendar", item.id)}><Trash2 size={13} /></button></article>)}</div> : <Empty icon={CalendarDays} label="No upcoming events" action="Add event" onAction={() => onCapture("calendar")} />}</Panel> : null}
    {widgets.includes("tasks") ? <Panel title="Focus list" icon={ListChecks} meta={`${snapshot.tasks.filter((item) => !item.completed).length} open`} action={<button type="button" onClick={() => onCapture("task")}><Plus size={14} />Task</button>}><TaskList items={snapshot.tasks.slice(0, 8)} onAction={onAction} onDelete={onDelete} /></Panel> : null}
    {widgets.includes("inbox") ? <Panel title="Unified inbox" icon={Inbox} meta={`${snapshot.inbox.filter((item) => !item.acknowledged).length} unread`}><NotificationList items={snapshot.inbox.slice(0, 7)} onAction={onAction} /></Panel> : null}
    {widgets.includes("deliveries") ? <Panel title="Deliveries" icon={Package} action={<button type="button" onClick={() => onCapture("package")}><Plus size={14} />Track</button>}><CompactItems items={snapshot.packages.map((item) => ({ id: item.id, title: item.description, detail: `${item.carrier} · ${item.status}`, date: item.estimatedDelivery }))} empty="No tracked deliveries" /></Panel> : null}
    {widgets.includes("shopping") ? <Panel title="Shopping" icon={ShoppingCart} action={<button type="button" onClick={() => onCapture("shopping")}><Plus size={14} />Item</button>}><div className="check-list">{snapshot.shopping.slice(0, 8).map((item) => <label className={item.completed ? "done" : ""} key={item.id}><input type="checkbox" checked={item.completed} onChange={() => void onAction({ tool: "shopping.toggle", target: item.id })} /><span>{item.quantity > 1 ? `${item.quantity}× ` : ""}{item.name}</span></label>)}</div></Panel> : null}
    {widgets.includes("media") ? <Panel title="Media requests" icon={Play} action={<button type="button" onClick={() => onCapture("media")}><Plus size={14} />Request</button>}><CompactItems items={snapshot.mediaRequests.map((item) => ({ id: item.id, title: item.title, detail: `${item.mediaType} · ${item.status}` }))} empty="No media requests" /></Panel> : null}
    {widgets.includes("notes") ? <Panel title="Quick notes" icon={NotebookPen} action={<button type="button" onClick={() => onCapture("note")}><Plus size={14} />Note</button>}><div className="note-list">{snapshot.notes.slice(0, 5).map((item) => <article key={item.id}><strong>{item.title}</strong><p>{item.body}</p>{item.tags.length ? <span>{item.tags.join(" · ")}</span> : null}</article>)}</div></Panel> : null}
    {widgets.includes("activity") ? <Panel title="Global activity" icon={Activity} meta={`${snapshot.activity.length} events`}><div className="global-activity">{snapshot.activity.slice(0, 9).map((item) => <article className={item.succeeded ? "" : "failed"} key={item.id}><i /><div><strong>{friendlyTool(item.tool)}</strong><span>{item.message}</span></div><time>{relative(item.occurredAt)}</time></article>)}</div></Panel> : null}
  </div>;
}

function PlannerView({ snapshot, onAction, onCapture, onDelete }: ViewProps) {
  return <div className="command-grid planner-grid"><Panel className="wide" title="Tasks" icon={ListChecks} action={<button type="button" onClick={() => onCapture("task")}><Plus size={14} />Task</button>}><TaskList items={snapshot.tasks} onAction={onAction} onDelete={onDelete} /></Panel><Panel title="Calendar" icon={CalendarDays} action={<button type="button" onClick={() => onCapture("calendar")}><Plus size={14} />Event</button>}><CompactItems items={snapshot.calendar.map((item) => ({ id: item.id, title: item.title, detail: item.calendar, date: item.startsAt }))} empty="No events" /></Panel><Panel title="Knowledge" icon={FileText} action={<button type="button" onClick={() => onCapture("note")}><Plus size={14} />Note</button>}><div className="note-list">{snapshot.notes.map((item) => <article key={item.id}><strong>{item.title}</strong><p>{item.body}</p><button type="button" title="Delete note" onClick={() => void onDelete("note", item.id)}><Trash2 size={13} /></button></article>)}</div></Panel><Panel title="Household" icon={Users} meta={`${snapshot.profiles.length} profiles`} action={<button type="button" onClick={() => onCapture("profile")}><Plus size={14} />Account</button>}><div className="profile-list">{snapshot.profiles.map((item) => <span key={item.id}><i style={{ background: item.color ?? "#5eead4" }} />{item.displayName}<small>{item.role}</small></span>)}</div></Panel></div>;
}

function HomeView({ snapshot, onAction }: Pick<ViewProps, "snapshot" | "onAction">) {
  const groups = groupBy(snapshot.homeEntities, (item) => item.area ?? "Other");
  return <div className="command-grid home-grid"><Panel className="wide" title="Rooms and devices" icon={Home} meta={`${snapshot.homeEntities.length} entities`}>{snapshot.homeEntities.length ? <div className="room-grid">{[...groups].map(([area, entities]) => <section key={area}><header><strong>{area}</strong><span>{entities.length}</span></header>{entities.slice(0, 10).map((entity) => <button type="button" className={entity.state === "on" ? "on" : ""} key={entity.id} onClick={() => void onAction({ tool: "homeassistant.call", target: entity.id, arguments: { domain: entity.domain, service: "toggle" } })}>{entityIcon(entity.domain)}<span><strong>{entity.name}</strong><small>{entity.state}</small></span></button>)}</section>)}</div> : <Empty icon={Home} label="Connect Home Assistant to load rooms and devices" />}</Panel><Panel title="Scenes" icon={Lightbulb}><div className="scene-grid">{["Good morning", "Away", "Movie", "Good night"].map((scene) => <button type="button" key={scene} onClick={() => void onAction({ tool: "homeassistant.call", target: `scene.${scene.toLowerCase().replaceAll(" ", "_")}`, arguments: { domain: "scene", service: "turn_on" } })}><Lightbulb size={17} />{scene}</button>)}</div></Panel><Panel title="Energy and climate" icon={Zap}><AssetSummary assets={snapshot.assets.filter((item) => ["Utilities", "Energy", "UPS", "Climate"].includes(item.category))} empty="Utility and energy signals appear here" /></Panel><Panel title="Security" icon={ShieldCheck}><AssetSummary assets={snapshot.assets.filter((item) => ["Security", "Camera", "Cameras", "Doorbell"].includes(item.category))} empty="Camera and security events appear here" /></Panel></div>;
}

function SystemsView({ snapshot, onCapture, onAction }: Pick<ViewProps, "snapshot" | "onCapture" | "onAction">) {
  const groups = groupBy(snapshot.assets, (item) => item.category);
  const expected = ["Network", "Backups", "Storage", "UPS", "Certificates", "GitHub", "Games", "Utilities", "Cameras", "Files", "Logs"];
  return <div className="command-grid systems-grid"><Panel className="wide" title="Operations inventory" icon={CircleGauge} action={<button type="button" onClick={() => onCapture("asset")}><Plus size={14} />Asset</button>}>{snapshot.assets.length ? <div className="asset-groups">{[...groups].map(([category, assets]) => <section key={category}><header><strong>{category}</strong><span>{assets.length}</span></header>{assets.map((asset) => <article key={asset.id}><i className={healthy(asset.status) ? "healthy" : "attention"} /><div><strong>{asset.name}</strong><span>{asset.detail ?? asset.status}</span></div><div>{Object.entries(asset.metrics).slice(0, 3).map(([key, value]) => <small key={key}>{key} <b>{value}</b></small>)}</div>{asset.url ? <a href={asset.url} target="_blank" rel="noreferrer"><ExternalLink size={14} /></a> : null}</article>)}</section>)}</div> : <Empty icon={Activity} label="Connect systems or add an asset" action="Add asset" onAction={() => onCapture("asset")} />}</Panel><Panel title="Coverage" icon={SquareStack}><div className="coverage-list">{expected.map((category) => <span key={category}><i className={groups.has(category) ? "covered" : ""} />{category}<small>{groups.get(category)?.length ?? 0}</small></span>)}</div></Panel><Panel title="Machine controls" icon={Power}><form className="wake-form" onSubmit={(event) => { event.preventDefault(); const data = new FormData(event.currentTarget); void onAction({ tool: "machine.wake", target: String(data.get("mac")) }); }}><input name="mac" placeholder="MAC address" required /><button type="submit"><Power size={15} />Wake</button></form><div className="control-note">Destructive computer actions remain agent-allowlisted and confirmation-gated.</div></Panel><Panel title="File and log workspace" icon={Archive}><div className="tool-rows"><button type="button"><FileText size={16} /><span><strong>Knowledge search</strong><small>Notes and incident history</small></span><ChevronRight size={14} /></button><button type="button"><Archive size={16} /><span><strong>Storage workspace</strong><small>Shares and tracked paths</small></span><ChevronRight size={14} /></button><button type="button"><Activity size={16} /><span><strong>Incident history</strong><small>Correlated system events</small></span><ChevronRight size={14} /></button></div></Panel></div>;
}

function AutomationView({ snapshot, onCapture, onAction, onDelete }: ViewProps) {
  return <div className="command-grid automation-grid"><Panel className="wide" title="Automations" icon={WandSparkles} action={<button type="button" onClick={() => onCapture("automation")}><Plus size={14} />Rule</button>}>{snapshot.automations.length ? <div className="automation-list">{snapshot.automations.map((rule) => <article className={!rule.enabled ? "disabled" : ""} key={rule.id}><div className="automation-icon"><Zap size={17} /></div><div><strong>{rule.name}</strong><span>When {rule.trigger}{rule.condition ? ` · if ${rule.condition}` : ""}</span><small>{rule.actionTool}{rule.actionTarget ? ` → ${rule.actionTarget}` : ""}{rule.lastRunAt ? ` · ${relative(rule.lastRunAt)}` : ""}</small></div><button type="button" title="Run automation" onClick={() => void onAction({ tool: "automation.run", target: rule.id })}><Play size={14} /></button><button type="button" title="Delete automation" onClick={() => void onDelete("automation", rule.id)}><Trash2 size={14} /></button></article>)}</div> : <Empty icon={WandSparkles} label="No automation rules" action="Create rule" onAction={() => onCapture("automation")} />}</Panel><Panel title="Event gateways" icon={Radio}><div className="gateway-list"><span><Radio size={16} />Authenticated webhooks</span><span><Network size={16} />MQTT HTTP bridge</span><span><Bell size={16} />Actionable notifications</span><span><AlarmClock size={16} />Schedules and reminders</span></div></Panel></div>;
}

function IntegrationView({ snapshot, onConfigure }: { snapshot: CommandCenterSnapshot; onConfigure: (item: IntegrationStatus) => void }) {
  const icon = (kind: string) => kind === "github" ? Github : kind.includes("home") ? Home : kind === "cameras" ? Camera : kind === "games" ? Gamepad2 : kind === "packages" ? Package : kind === "ups" || kind === "utilities" ? Zap : kind === "ollama" ? Bot : kind === "ntfy" ? Bell : Network;
  return <div className="integration-grid">{snapshot.integrations.map((item) => { const Icon = icon(item.kind); return <button type="button" className={`integration-card ${item.enabled ? "enabled" : ""}`} key={item.id} onClick={() => onConfigure(item)}><Icon size={21} /><div><strong>{item.name}</strong><span>{item.capabilities.slice(0, 3).join(" · ")}</span><small>{item.status}</small></div><i className={item.connected ? "connected" : item.enabled ? "pending" : ""} /></button>; })}</div>;
}

function MachineActionBar({ onAction }: { onAction: ViewProps["onAction"] }) {
  return <div className="machine-action-bar"><span><ShieldCheck size={15} />Agent-approved controls</span>{[
    ["Lock", "machine.lock"], ["Sleep", "machine.sleep"], ["Restart", "machine.restart"], ["Shut down", "machine.shutdown"]
  ].map(([label, tool]) => <button type="button" key={tool} onClick={() => void onAction({ tool, target: null })}><Power size={14} />{label}</button>)}</div>;
}

function SystemWorkspaceBar({ onOpen }: { onOpen: (kind: "files" | "logs") => void }) {
  return <div className="system-workspace-bar"><span><Archive size={15} />Windows workspace</span><button type="button" onClick={() => onOpen("files")}><Archive size={14} />Browse files</button><button type="button" onClick={() => onOpen("logs")}><Activity size={14} />System logs</button></div>;
}

function WorkspaceDialog({ kind, onClose }: { kind: "files" | "logs"; onClose: () => void }) {
  const [files, setFiles] = useState<FileWorkspaceEntry[]>([]); const [logs, setLogs] = useState<SystemLogEntry[]>([]); const [path, setPath] = useState<string | undefined>(); const [error, setError] = useState<string | null>(null);
  useEffect(() => { if (kind === "files") void browseCommandCenterFiles(path).then(setFiles).catch((ex: unknown) => setError(ex instanceof Error ? ex.message : "Files unavailable.")); else void getCommandCenterLogs().then(setLogs).catch((ex: unknown) => setError(ex instanceof Error ? ex.message : "Logs unavailable.")); }, [kind, path]);
  return <div className="command-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><section className="command-modal workspace-modal"><header><div><span className="section-kicker">Windows workspace</span><h3>{kind === "files" ? "Files" : "System logs"}</h3></div><button type="button" onClick={onClose}><X size={17} /></button></header>{error ? <div className="workspace-error">{error}</div> : kind === "files" ? <div className="workspace-list">{path ? <button type="button" onClick={() => setPath(path.replace(/[\\/][^\\/]+$/, ""))}><Archive size={15} /><span><strong>Parent folder</strong><small>{path}</small></span></button> : null}{files.map((item) => <button type="button" disabled={!item.isDirectory} key={item.path} onClick={() => item.isDirectory && setPath(item.path)}>{item.isDirectory ? <Archive size={15} /> : <FileText size={15} />}<span><strong>{item.name}</strong><small>{item.isDirectory ? "Folder" : formatBytes(item.sizeBytes)} · {formatDate(item.updatedAt)}</small></span></button>)}</div> : <div className="system-log-list">{logs.map((item, index) => <article className={item.level.toLowerCase()} key={`${item.occurredAt}-${index}`}><time>{new Date(item.occurredAt).toLocaleString()}</time><strong>{item.source}</strong><span>{item.message}</span></article>)}</div>}</section></div>;
}

interface ViewProps { snapshot: CommandCenterSnapshot; onAction: (request: CommandCenterActionRequest) => Promise<void>; onCapture: (kind: CaptureKind) => void; onDelete: (kind: string, id: string) => Promise<void>; }
function Panel({ title, icon: Icon, meta, action, className = "", children }: { title: string; icon: typeof Home; meta?: string; action?: React.ReactNode; className?: string; children: React.ReactNode }) { return <section className={`command-panel ${className}`}><header><Icon size={17} /><strong>{title}</strong>{meta ? <span>{meta}</span> : null}{action}</header><div className="command-panel-body">{children}</div></section>; }
function Metric({ icon: Icon, value, label, alert = false }: { icon: typeof Home; value: number; label: string; alert?: boolean }) { return <div className={alert ? "alert" : ""}><Icon size={17} /><strong>{value}</strong><span>{label}</span></div>; }
function Empty({ icon: Icon, label, action, onAction }: { icon: typeof Home; label: string; action?: string; onAction?: () => void }) { return <div className="command-empty"><Icon size={20} /><span>{label}</span>{action && onAction ? <button type="button" onClick={onAction}><Plus size={14} />{action}</button> : null}</div>; }
function TaskList({ items, onAction, onDelete }: { items: CommandCenterSnapshot["tasks"]; onAction: ViewProps["onAction"]; onDelete: ViewProps["onDelete"] }) { return <div className="task-list">{items.length ? items.map((item) => <article className={item.completed ? "done" : ""} key={item.id}><button type="button" title={item.completed ? "Reopen task" : "Complete task"} onClick={() => void onAction({ tool: "task.toggle", target: item.id, arguments: { completed: String(!item.completed) } })}><Check size={14} /></button><div><strong>{item.title}</strong><span>{item.list}{item.dueAt ? ` · ${formatDate(item.dueAt)}` : ""}</span></div><b className={item.priority.toLowerCase()}>{item.priority}</b><button type="button" title="Delete task" onClick={() => void onDelete("task", item.id)}><Trash2 size={13} /></button></article>) : <Empty icon={ListChecks} label="No tasks" />}</div>; }
function CompactItems({ items, empty }: { items: Array<{ id: string; title: string; detail: string; date?: string | null }>; empty: string }) { return items.length ? <div className="compact-items">{items.slice(0, 9).map((item) => <article key={item.id}><div><strong>{item.title}</strong><span>{item.detail}</span></div>{item.date ? <time>{formatDate(item.date)}</time> : null}</article>)}</div> : <div className="mini-empty">{empty}</div>; }
function NotificationList({ items, onAction }: { items: CommandCenterSnapshot["inbox"]; onAction: ViewProps["onAction"] }) { return <div className="command-inbox">{items.map((item) => <article className={item.acknowledged ? "read" : item.severity.toLowerCase()} key={item.id}><i /><div><span>{item.source} · {relative(item.createdAt)}</span><strong>{item.title}</strong><p>{item.message}</p>{item.actions?.length ? <div className="inbox-actions">{item.actions.map((action) => <button type="button" key={`${action.tool}-${action.target}`} onClick={() => void onAction({ tool: action.tool, target: action.target, confirmed: !action.requiresConfirmation })}>{action.label}</button>)}</div> : null}</div>{!item.acknowledged ? <button type="button" title="Acknowledge" onClick={() => void onAction({ tool: "notification.ack", target: item.id })}><Check size={14} /></button> : null}</article>)}</div>; }
function AssetSummary({ assets, empty }: { assets: CommandCenterSnapshot["assets"]; empty: string }) { return assets.length ? <div className="compact-items">{assets.slice(0, 8).map((item) => <article key={item.id}><i className={healthy(item.status) ? "healthy" : "attention"} /><div><strong>{item.name}</strong><span>{item.detail ?? item.status}</span></div></article>)}</div> : <div className="mini-empty">{empty}</div>; }

function CaptureDialog({ kind, busy, onClose, onSave }: { kind: CaptureKind; busy: boolean; onClose: () => void; onSave: (request: CommandCenterItemRequest) => Promise<void> }) {
  const labels: Record<CaptureKind, string> = { task: "New task", calendar: "New event", note: "New note", shopping: "Shopping item", package: "Track package", media: "Media request", automation: "New automation", asset: "Track asset", profile: "Household account" };
  return <div className="command-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><form className="command-modal" onSubmit={(event) => { event.preventDefault(); const data = new FormData(event.currentTarget); const fields: Record<string, string> = {}; ["priority", "location", "tags", "quantity", "carrier", "trackingNumber", "status", "mediaType", "trigger", "condition", "actionTool", "actionTarget", "assetStatus", "url", "username", "password", "role", "color"].forEach((key) => { const value = String(data.get(key) ?? ""); if (value) fields[key === "assetStatus" ? "status" : key] = value; }); void onSave({ kind, title: String(data.get("title")), details: String(data.get("details") ?? "") || null, category: String(data.get("category") ?? "") || null, date: String(data.get("date") ?? "") || null, fields }); }}><header><div><span className="section-kicker">Quick capture</span><h3>{labels[kind]}</h3></div><button type="button" onClick={onClose}><X size={17} /></button></header><label><span>{kind === "shopping" ? "Item" : kind === "package" ? "Description" : kind === "profile" ? "Display name" : "Title"}</span><input name="title" autoFocus required maxLength={240} /></label>{["task", "note", "asset"].includes(kind) ? <label><span>Details</span><textarea name="details" rows={3} /></label> : null}{["task", "calendar", "shopping", "asset"].includes(kind) ? <label><span>{kind === "asset" ? "Category" : kind === "shopping" ? "List" : kind === "calendar" ? "Calendar" : "List"}</span><input name="category" placeholder={kind === "asset" ? "Network, Backups, UPS, GitHub..." : "Personal"} /></label> : null}{["task", "calendar", "package"].includes(kind) ? <label><span>{kind === "package" ? "Estimated delivery" : kind === "calendar" ? "Starts" : "Due"}</span><input name="date" type="datetime-local" /></label> : null}{kind === "task" ? <label><span>Priority</span><select name="priority"><option>Normal</option><option>High</option><option>Urgent</option><option>Low</option></select></label> : null}{kind === "note" ? <label><span>Tags</span><input name="tags" placeholder="home, project" /></label> : null}{kind === "shopping" ? <label><span>Quantity</span><input name="quantity" type="number" min="1" defaultValue="1" /></label> : null}{kind === "package" ? <><label><span>Carrier</span><input name="carrier" required /></label><label><span>Tracking number</span><input name="trackingNumber" /></label><label><span>Status</span><input name="status" defaultValue="Tracking" /></label></> : null}{kind === "media" ? <><label><span>Type</span><select name="mediaType"><option>Movie</option><option>Series</option><option>Music</option><option>Book</option><option>Game</option></select></label><label><span>Status</span><select name="status"><option>Requested</option><option>Approved</option><option>Available</option></select></label></> : null}{kind === "automation" ? <><label><span>Trigger</span><input name="trigger" placeholder="daily at 07:00 or webhook.event" required /></label><label><span>Condition</span><input name="condition" placeholder="optional" /></label><label><span>Action</span><select name="actionTool"><option value="notification.create">Create notification</option><option value="notification.send">Send notification</option><option value="homeassistant.call">Home Assistant action</option><option value="webhook.send">Webhook</option><option value="mqtt.publish">MQTT bridge</option></select></label><label><span>Target</span><input name="actionTarget" /></label></> : null}{kind === "asset" ? <><label><span>Status</span><input name="assetStatus" defaultValue="Online" /></label><label><span>URL</span><input name="url" type="url" /></label></> : null}{kind === "profile" ? <><label><span>Username</span><input name="username" required /></label><label><span>Password</span><input name="password" type="password" minLength={8} required /></label><label><span>Role</span><select name="role"><option>Member</option><option>Viewer</option><option>Administrator</option></select></label><label><span>Color</span><input name="color" type="color" defaultValue="#5eead4" /></label></> : null}<footer><button type="button" onClick={onClose}>Cancel</button><button className="primary" type="submit" disabled={busy}><Check size={15} />Save</button></footer></form></div>;
}

function IntegrationDialog({ integration, busy, onClose, onSave }: { integration: IntegrationStatus; busy: boolean; onClose: () => void; onSave: (request: { name: string; baseUrl?: string | null; enabled: boolean; secret?: string | null; settings?: Record<string, string> }) => Promise<void> }) {
  return <div className="command-modal-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}><form className="command-modal integration-modal" onSubmit={(event) => { event.preventDefault(); const data = new FormData(event.currentTarget); const settings: Record<string, string> = {}; ["topic", "model", "roots", "prefix", "allowedUserIds", "allowedChannelIds", "allowedGuildIds"].forEach((key) => { if (data.has(key)) settings[key] = String(data.get(key) ?? ""); }); void onSave({ name: String(data.get("name")), baseUrl: String(data.get("baseUrl") ?? "") || null, enabled: data.get("enabled") === "on", secret: String(data.get("secret") ?? "") || null, settings }); }}><header><div><span className="section-kicker">Integration</span><h3>{integration.name}</h3></div><button type="button" onClick={onClose}><X size={17} /></button></header><label className="switch-row"><input name="enabled" type="checkbox" defaultChecked={integration.enabled} /><span>Enabled</span></label><label><span>Name</span><input name="name" defaultValue={integration.name} required /></label>{integration.id !== "discord" ? <label><span>Endpoint</span><input name="baseUrl" type="url" defaultValue={integration.baseUrl ?? ""} placeholder="https://..." /></label> : null}<label><span>{integration.hasSecret ? "Replace credential" : integration.id === "discord" ? "Bot token" : "Credential"}</span><input name="secret" type="password" autoComplete="new-password" placeholder={integration.hasSecret ? "Leave blank to keep saved value" : integration.id === "discord" ? "Discord bot token" : "Token or API key"} /></label>{integration.id === "ntfy" ? <label><span>Topic</span><input name="topic" defaultValue={integration.settings.topic ?? ""} placeholder="homedashboard" /></label> : null}{integration.id === "ollama" ? <label><span>Model</span><input name="model" defaultValue={integration.settings.model ?? ""} placeholder="qwen3:4b" /></label> : null}{integration.id === "windows" ? <label><span>Allowed roots</span><input name="roots" defaultValue={integration.settings.roots ?? ""} placeholder="D:\\Media; E:\\Backups" /></label> : null}{integration.id === "discord" ? <><label><span>Command prefix</span><input name="prefix" defaultValue={integration.settings.prefix ?? ""} placeholder="!hd" /></label><label><span>Allowed user IDs</span><input name="allowedUserIds" inputMode="numeric" defaultValue={integration.settings.allowedUserIds ?? ""} placeholder="Required · comma-separated" /></label><label><span>Allowed channel IDs</span><input name="allowedChannelIds" inputMode="numeric" defaultValue={integration.settings.allowedChannelIds ?? ""} placeholder="Optional · comma-separated" /></label><label><span>Allowed server IDs</span><input name="allowedGuildIds" inputMode="numeric" defaultValue={integration.settings.allowedGuildIds ?? ""} placeholder="Optional · comma-separated" /></label></> : null}<div className="capability-chips">{integration.capabilities.map((item) => <span key={item}>{item}</span>)}</div><footer><button type="button" onClick={onClose}>Cancel</button><button className="primary" type="submit" disabled={busy}><Check size={15} />Save</button></footer></form></div>;
}

function CommandPalette({ snapshot, onClose, onTab, onAsk, onCapture }: { snapshot: CommandCenterSnapshot; onClose: () => void; onTab: (tab: Tab) => void; onAsk: (prompt: string) => void; onCapture: (kind: CaptureKind) => void }) {
  const [query, setQuery] = useState(""); const [results, setResults] = useState<SearchResult[]>([]); const input = useRef<HTMLInputElement>(null);
  useEffect(() => input.current?.focus(), []);
  useEffect(() => { const handle = window.setTimeout(() => { if (query.trim()) void searchCommandCenter(query).then(setResults); else setResults([]); }, 180); return () => window.clearTimeout(handle); }, [query]);
  const commands = [{ label: "Open Today", run: () => onTab("today") }, { label: "Open planner", run: () => onTab("planner") }, { label: "Open systems", run: () => onTab("systems") }, { label: "Add task", run: () => onCapture("task") }, { label: "Add note", run: () => onCapture("note") }, { label: "Track delivery", run: () => onCapture("package") }].filter((item) => !query || item.label.toLowerCase().includes(query.toLowerCase()));
  return <div className="palette-backdrop" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
    <div className="command-palette">
      <header><Search size={18} /><input ref={input} value={query} onChange={(event) => setQuery(event.target.value)} onKeyDown={(event) => { if (event.key === "Escape") onClose(); if (event.key === "Enter" && query.trim()) onAsk(query); }} placeholder="Search or ask HomeDashboard" /><button type="button" onClick={onClose}><X size={15} /></button></header>
      <div className="palette-results">
        {commands.map((item) => <button type="button" key={item.label} onClick={item.run}><Command size={15} /><span>{item.label}</span><ChevronRight size={14} /></button>)}
        {results.map((item) => <button type="button" key={`${item.kind}-${item.id}`} onClick={() => onAsk(`Tell me about ${item.title}`)}><ResultIcon kind={item.kind} /><span><strong>{item.title}</strong><small>{item.kind}{item.subtitle ? ` · ${item.subtitle}` : ""}</small></span><ChevronRight size={14} /></button>)}
        {query && !results.length ? <button className="ask-result" type="button" onClick={() => onAsk(query)}><Bot size={16} /><span><strong>Ask assistant</strong><small>{query}</small></span><ChevronRight size={14} /></button> : null}
      </div>
      <footer><span>{snapshot.tasks.filter((item) => !item.completed).length} tasks</span><span>{snapshot.inbox.filter((item) => !item.acknowledged).length} unread</span></footer>
    </div>
  </div>;
}

function VoiceButton({ onResult }: { onResult: (value: string) => void }) {
  const [listening, setListening] = useState(false);
  function listen() {
    const SpeechRecognition = (window as unknown as { SpeechRecognition?: SpeechRecognitionConstructor; webkitSpeechRecognition?: SpeechRecognitionConstructor }).SpeechRecognition ?? (window as unknown as { webkitSpeechRecognition?: SpeechRecognitionConstructor }).webkitSpeechRecognition;
    if (!SpeechRecognition) return;
    const recognition = new SpeechRecognition(); recognition.lang = navigator.language; recognition.interimResults = false;
    recognition.onstart = () => setListening(true); recognition.onend = () => setListening(false); recognition.onresult = (event) => onResult(event.results[0][0].transcript); recognition.start();
  }
  return <button className={listening ? "listening" : ""} type="button" title="Voice input" onClick={listen}><Mic size={16} /></button>;
}
interface SpeechRecognitionConstructor { new(): { lang: string; interimResults: boolean; onstart: (() => void) | null; onend: (() => void) | null; onresult: ((event: { results: { [index: number]: { [index: number]: { transcript: string } } } }) => void) | null; start(): void; }; }
function ResultIcon({ kind }: { kind: string }) { const Icon = kind === "Task" ? ListChecks : kind === "Calendar" ? CalendarDays : kind === "Home" ? Home : kind === "System" ? Server : kind === "Package" ? Package : FileText; return <Icon size={15} />; }
function entityIcon(domain: string) { const Icon = domain === "light" ? Lightbulb : domain === "camera" ? Camera : domain === "switch" ? Zap : domain === "climate" ? CloudLightning : Home; return <Icon size={17} />; }
function modeIcon(mode: string) { const Icon = mode === "Gaming" ? Gamepad2 : mode === "Away" ? ShieldCheck : mode === "Sleep" ? AlarmClock : mode === "Work" ? Cpu : mode === "Movie" ? Play : mode === "Guest" ? Users : Home; return <Icon size={15} />; }
function friendlyTool(tool: string) { return tool.split(".").join(" "); }
function healthy(status: string) { return ["online", "healthy", "ok", "connected", "available"].includes(status.toLowerCase()); }
function today(item: { startsAt: string }) { return new Date(item.startsAt).toDateString() === new Date().toDateString(); }
function relative(value: string) { const minutes = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 60_000)); return minutes < 60 ? `${minutes}m` : minutes < 1440 ? `${Math.floor(minutes / 60)}h` : `${Math.floor(minutes / 1440)}d`; }
function formatDate(value: string) { const date = new Date(value); return date.toLocaleDateString([], { month: "short", day: "numeric" }) + (date.getHours() || date.getMinutes() ? ` · ${date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}` : ""); }
function formatBytes(value: number) { if (value < 1024) return `${value} B`; const units = ["KB", "MB", "GB", "TB"]; let size = value / 1024; let index = 0; while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; } return `${size.toFixed(size >= 10 ? 0 : 1)} ${units[index]}`; }
function groupBy<T>(items: T[], key: (item: T) => string) { const groups = new Map<string, T[]>(); items.forEach((item) => groups.set(key(item), [...(groups.get(key(item)) ?? []), item])); return groups; }
function readStoredArray(key: string, fallback: string[]) { try { const value = JSON.parse(localStorage.getItem(key) ?? "null"); return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : fallback; } catch { return fallback; } }
