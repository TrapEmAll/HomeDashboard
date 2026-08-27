import { useMemo, useState } from "react";
import {
  ArrowUpDown, BellRing, CalendarClock, CalendarDays, Check, CheckCheck, Clock3, Copy, Filter,
  ListChecks, NotebookPen, Pin, PinOff, Plus, Search, ShoppingBasket, Tag, Trash2
} from "lucide-react";
import type {
  CommandCenterBatchRequest, CommandCenterItemRequest, CommandCenterSnapshot, PersonalTask, QuickNote
} from "../types/commandCenter";

interface Props {
  snapshot: CommandCenterSnapshot;
  busy: boolean;
  onBatch: (request: CommandCenterBatchRequest) => Promise<void>;
  onSave: (request: CommandCenterItemRequest) => Promise<void>;
  onCapture: (kind: "task" | "calendar" | "note" | "shopping") => void;
}

const priorityWeight = { Urgent: 0, High: 1, Normal: 2, Low: 3 } as const;

export function ProductivityWorkspace({ snapshot, busy, onBatch, onSave, onCapture }: Props) {
  const [taskQuery, setTaskQuery] = useState("");
  const [taskList, setTaskList] = useStored("homedashboard-plan-task-list", "All");
  const [taskPriority, setTaskPriority] = useStored("homedashboard-plan-task-priority", "All");
  const [taskSort, setTaskSort] = useStored("homedashboard-plan-task-sort", "Smart");
  const [calendarName, setCalendarName] = useStored("homedashboard-plan-calendar", "All");
  const [shoppingList, setShoppingList] = useStored("homedashboard-plan-shopping-list", "All");
  const [noteQuery, setNoteQuery] = useState("");
  const [noteTag, setNoteTag] = useStored("homedashboard-plan-note-tag", "All");
  const [severity, setSeverity] = useStored("homedashboard-plan-inbox", "Unread");
  const [copied, setCopied] = useState(false);

  const taskLists = unique(snapshot.tasks.map((item) => item.list));
  const calendars = unique(snapshot.calendar.map((item) => item.calendar));
  const shoppingLists = unique(snapshot.shopping.map((item) => item.list));
  const noteTags = unique(snapshot.notes.flatMap((item) => item.tags));

  const filteredTasks = useMemo(() => {
    const needle = taskQuery.trim().toLowerCase();
    return [...snapshot.tasks]
      .filter((item) => taskList === "All" || item.list === taskList)
      .filter((item) => taskPriority === "All" || item.priority === taskPriority)
      .filter((item) => !needle || `${item.title} ${item.details ?? ""}`.toLowerCase().includes(needle))
      .sort((left, right) => compareTasks(left, right, taskSort));
  }, [snapshot.tasks, taskList, taskPriority, taskQuery, taskSort]);
  const taskGroups = groupTasks(filteredTasks);
  const visibleOpenTasks = filteredTasks.filter((item) => !item.completed);
  const visibleCompletedTasks = filteredTasks.filter((item) => item.completed);

  const weekStart = startOfDay(new Date());
  const weekEnd = addDays(weekStart, 7);
  const weekDays = Array.from({ length: 7 }, (_, index) => addDays(weekStart, index));
  const weekEvents = snapshot.calendar.filter((item) => {
    const date = new Date(item.startsAt);
    return date >= weekStart && date < weekEnd && (calendarName === "All" || item.calendar === calendarName);
  });

  const visibleShopping = snapshot.shopping.filter((item) => shoppingList === "All" || item.list === shoppingList);
  const purchased = visibleShopping.filter((item) => item.completed).length;
  const shoppingProgress = visibleShopping.length ? Math.round((purchased / visibleShopping.length) * 100) : 0;

  const visibleNotes = snapshot.notes
    .filter((item) => noteTag === "All" || item.tags.includes(noteTag))
    .filter((item) => !noteQuery.trim() || `${item.title} ${item.body} ${item.tags.join(" ")}`.toLowerCase().includes(noteQuery.trim().toLowerCase()))
    .sort((left, right) => Number(right.pinned) - Number(left.pinned) || Date.parse(right.updatedAt) - Date.parse(left.updatedAt));

  const visibleInbox = snapshot.inbox.filter((item) => severity === "All"
    || (severity === "Unread" ? !item.acknowledged : item.severity === severity));
  const unreadVisible = visibleInbox.filter((item) => !item.acknowledged);

  async function completeTasks() {
    await onBatch({ actions: visibleOpenTasks.map((item) => ({ tool: "task.toggle", target: item.id, arguments: { completed: "true" } })) });
  }

  async function clearCompletedTasks() {
    if (!visibleCompletedTasks.length || !window.confirm(`Remove ${visibleCompletedTasks.length} completed task(s)?`)) return;
    await onBatch({ deletes: visibleCompletedTasks.map((item) => ({ kind: "task", id: item.id })) });
  }

  async function postpone(item: PersonalTask) {
    const due = item.dueAt ? new Date(item.dueAt) : new Date();
    due.setDate(due.getDate() + 1);
    await onSave({ kind: "task", id: item.id, title: item.title, details: item.details, category: item.list, date: due.toISOString(), fields: { priority: item.priority, completed: String(item.completed) } });
  }

  async function purchaseVisible() {
    const open = visibleShopping.filter((item) => !item.completed);
    await onBatch({ actions: open.map((item) => ({ tool: "shopping.toggle", target: item.id })) });
  }

  async function clearPurchased() {
    const done = visibleShopping.filter((item) => item.completed);
    if (!done.length || !window.confirm(`Remove ${done.length} purchased item(s)?`)) return;
    await onBatch({ deletes: done.map((item) => ({ kind: "shopping", id: item.id })) });
  }

  async function copyShopping() {
    const text = visibleShopping.filter((item) => !item.completed).map((item) => `${item.quantity > 1 ? `${item.quantity} x ` : ""}${item.name}`).join("\n");
    await copyText(text);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1600);
  }

  async function togglePin(note: QuickNote) {
    await onSave({ kind: "note", id: note.id, title: note.title, details: note.body, fields: { tags: note.tags.join(","), pinned: String(!note.pinned) } });
  }

  async function acknowledgeVisible() {
    await onBatch({ actions: unreadVisible.map((item) => ({ tool: "notification.ack", target: item.id })) });
  }

  return <div className="productivity-workspace">
    <section className="plan-summary" aria-label="Planning summary">
      <SummaryMetric label="Open tasks" value={snapshot.tasks.filter((item) => !item.completed).length} tone="cyan" />
      <SummaryMetric label="Overdue" value={snapshot.tasks.filter(isOverdue).length} tone="red" />
      <SummaryMetric label="Next 7 days" value={weekEvents.length} tone="amber" />
      <SummaryMetric label="Shopping left" value={snapshot.shopping.filter((item) => !item.completed).length} tone="green" />
      <SummaryMetric label="Unread alerts" value={snapshot.inbox.filter((item) => !item.acknowledged).length} tone="violet" />
    </section>

    <div className="productivity-grid">
      <section className="productivity-panel task-workbench">
        <PanelHeader icon={ListChecks} title="Task workbench" meta={`${filteredTasks.length} visible`} action={<button type="button" onClick={() => onCapture("task")}><Plus size={14} />Task</button>} />
        <div className="productivity-filters task-filters">
          <label className="search-control"><Search size={14} /><input value={taskQuery} onChange={(event) => setTaskQuery(event.target.value)} placeholder="Search tasks" /></label>
          <SelectControl icon={Filter} label="List" value={taskList} values={withCurrent(["All", ...taskLists], taskList)} onChange={setTaskList} />
          <SelectControl icon={Filter} label="Priority" value={taskPriority} values={["All", "Urgent", "High", "Normal", "Low"]} onChange={setTaskPriority} />
          <SelectControl icon={ArrowUpDown} label="Sort" value={taskSort} values={["Smart", "Due date", "Priority", "Newest"]} onChange={setTaskSort} />
        </div>
        <div className="bulk-toolbar">
          <span>{visibleOpenTasks.length} open · {visibleCompletedTasks.length} completed</span>
          <button type="button" disabled={busy || !visibleOpenTasks.length} onClick={() => void completeTasks()}><CheckCheck size={13} />Complete visible</button>
          <button type="button" disabled={busy || !visibleCompletedTasks.length} onClick={() => void clearCompletedTasks()}><Trash2 size={13} />Clear completed</button>
        </div>
        <div className="task-groups">
          {taskGroups.map(([group, items]) => <section key={group}><header><span>{group}</span><b>{items.length}</b></header>{items.map((item) => <article className={item.completed ? "done" : isOverdue(item) ? "overdue" : ""} key={item.id}>
            <button className="task-check" type="button" title={item.completed ? "Reopen task" : "Complete task"} onClick={() => void onBatch({ actions: [{ tool: "task.toggle", target: item.id, arguments: { completed: String(!item.completed) } }] })}><Check size={13} /></button>
            <div><strong>{item.title}</strong><span>{item.list}{item.dueAt ? ` · ${formatDate(item.dueAt)}` : " · No due date"}</span></div>
            <b className={`priority ${item.priority.toLowerCase()}`}>{item.priority}</b>
            {!item.completed ? <button className="postpone" type="button" title="Postpone one day" onClick={() => void postpone(item)}><CalendarClock size={13} /></button> : null}
          </article>)}</section>)}
          {!filteredTasks.length ? <EmptyState icon={ListChecks} text="No tasks match these filters" /> : null}
        </div>
      </section>

      <section className="productivity-panel agenda-workbench">
        <PanelHeader icon={CalendarDays} title="Seven-day agenda" meta={`${weekEvents.length} events`} action={<button type="button" onClick={() => onCapture("calendar")}><Plus size={14} />Event</button>} />
        <div className="productivity-filters"><SelectControl icon={Filter} label="Calendar" value={calendarName} values={withCurrent(["All", ...calendars], calendarName)} onChange={setCalendarName} /></div>
        <div className="week-agenda">{weekDays.map((day) => {
          const events = weekEvents.filter((item) => sameDay(new Date(item.startsAt), day));
          return <section className={sameDay(day, new Date()) ? "today" : ""} key={day.toISOString()}><header><span>{day.toLocaleDateString([], { weekday: "short" })}</span><b>{day.getDate()}</b></header><div>{events.map((item) => <article key={item.id}><time>{item.allDay ? "All day" : new Date(item.startsAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}</time><strong>{item.title}</strong><span>{item.location ?? item.calendar}</span></article>)}{!events.length ? <small>Open</small> : null}</div></section>;
        })}</div>
      </section>

      <section className="productivity-panel shopping-workbench">
        <PanelHeader icon={ShoppingBasket} title="Shopping control" meta={`${shoppingProgress}% purchased`} action={<button type="button" onClick={() => onCapture("shopping")}><Plus size={14} />Item</button>} />
        <div className="shopping-progress"><i style={{ width: `${shoppingProgress}%` }} /><span>{purchased} of {visibleShopping.length}</span></div>
        <div className="productivity-filters"><SelectControl icon={Filter} label="List" value={shoppingList} values={withCurrent(["All", ...shoppingLists], shoppingList)} onChange={setShoppingList} /></div>
        <div className="bulk-toolbar shopping-actions">
          <button type="button" disabled={busy || !visibleShopping.some((item) => !item.completed)} onClick={() => void purchaseVisible()}><CheckCheck size={13} />Buy all</button>
          <button type="button" disabled={!visibleShopping.some((item) => !item.completed)} onClick={() => void copyShopping()}><Copy size={13} />{copied ? "Copied" : "Copy list"}</button>
          <button type="button" disabled={busy || !purchased} onClick={() => void clearPurchased()}><Trash2 size={13} />Clear</button>
        </div>
        <div className="shopping-control-list">{visibleShopping.map((item) => <label className={item.completed ? "done" : ""} key={item.id}><input type="checkbox" checked={item.completed} onChange={() => void onBatch({ actions: [{ tool: "shopping.toggle", target: item.id }] })} /><span>{item.quantity > 1 ? `${item.quantity} x ` : ""}{item.name}</span><small>{item.list}</small></label>)}{!visibleShopping.length ? <EmptyState icon={ShoppingBasket} text="This list is empty" /> : null}</div>
      </section>

      <section className="productivity-panel notes-workbench">
        <PanelHeader icon={NotebookPen} title="Knowledge notes" meta={`${visibleNotes.length} notes`} action={<button type="button" onClick={() => onCapture("note")}><Plus size={14} />Note</button>} />
        <div className="productivity-filters note-filters"><label className="search-control"><Search size={14} /><input value={noteQuery} onChange={(event) => setNoteQuery(event.target.value)} placeholder="Search notes" /></label><SelectControl icon={Tag} label="Tag" value={noteTag} values={withCurrent(["All", ...noteTags], noteTag)} onChange={setNoteTag} /></div>
        <div className="knowledge-list">{visibleNotes.map((note) => <article className={note.pinned ? "pinned" : ""} key={note.id}><button type="button" title={note.pinned ? "Unpin note" : "Pin note"} onClick={() => void togglePin(note)}>{note.pinned ? <PinOff size={13} /> : <Pin size={13} />}</button><div><strong>{note.title}</strong><p>{note.body}</p>{note.tags.length ? <span>{note.tags.join(" · ")}</span> : null}</div></article>)}{!visibleNotes.length ? <EmptyState icon={NotebookPen} text="No notes match this view" /> : null}</div>
      </section>

      <section className="productivity-panel inbox-workbench">
        <PanelHeader icon={BellRing} title="Attention inbox" meta={`${unreadVisible.length} unread`} />
        <div className="productivity-filters"><SelectControl icon={Filter} label="Severity" value={severity} values={["Unread", "All", "Critical", "Warning", "Info"]} onChange={setSeverity} /></div>
        <div className="bulk-toolbar"><span>{visibleInbox.length} visible</span><button type="button" disabled={busy || !unreadVisible.length} onClick={() => void acknowledgeVisible()}><CheckCheck size={13} />Acknowledge visible</button></div>
        <div className="attention-list">{visibleInbox.slice(0, 30).map((item) => <article className={`${item.severity.toLowerCase()} ${item.acknowledged ? "read" : ""}`} key={item.id}><i /><div><span>{item.source} · {relative(item.createdAt)}</span><strong>{item.title}</strong><p>{item.message}</p></div>{!item.acknowledged ? <button type="button" title="Acknowledge" onClick={() => void onBatch({ actions: [{ tool: "notification.ack", target: item.id }] })}><Check size={13} /></button> : null}</article>)}{!visibleInbox.length ? <EmptyState icon={BellRing} text="No alerts match this view" /> : null}</div>
      </section>
    </div>
  </div>;
}

function PanelHeader({ icon: Icon, title, meta, action }: { icon: typeof ListChecks; title: string; meta: string; action?: React.ReactNode }) {
  return <header className="productivity-panel-header"><Icon size={17} /><strong>{title}</strong><span>{meta}</span>{action}</header>;
}

function SummaryMetric({ label, value, tone }: { label: string; value: number; tone: string }) {
  return <div className={tone}><strong>{value}</strong><span>{label}</span></div>;
}

function SelectControl({ icon: Icon, label, value, values, onChange }: { icon: typeof Filter; label: string; value: string; values: string[]; onChange: (value: string) => void }) {
  return <label className="select-control"><Icon size={13} /><span>{label}</span><select value={value} onChange={(event) => onChange(event.target.value)}>{values.map((item) => <option key={item}>{item}</option>)}</select></label>;
}

function EmptyState({ icon: Icon, text }: { icon: typeof ListChecks; text: string }) {
  return <div className="productivity-empty"><Icon size={18} /><span>{text}</span></div>;
}

function compareTasks(left: PersonalTask, right: PersonalTask, sort: string) {
  if (sort === "Priority") return priorityWeight[left.priority] - priorityWeight[right.priority];
  if (sort === "Newest") return Date.parse(right.createdAt) - Date.parse(left.createdAt);
  if (sort === "Due date") return dueTime(left) - dueTime(right);
  return Number(left.completed) - Number(right.completed)
    || taskBucketRank(left) - taskBucketRank(right)
    || priorityWeight[left.priority] - priorityWeight[right.priority]
    || dueTime(left) - dueTime(right);
}

function groupTasks(tasks: PersonalTask[]): Array<[string, PersonalTask[]]> {
  const order = ["Overdue", "Today", "Upcoming", "No date", "Completed"];
  const groups = new Map<string, PersonalTask[]>();
  tasks.forEach((item) => { const key = taskBucket(item); groups.set(key, [...(groups.get(key) ?? []), item]); });
  return order.filter((key) => groups.has(key)).map((key) => [key, groups.get(key)!]);
}

function taskBucket(item: PersonalTask) {
  if (item.completed) return "Completed";
  if (!item.dueAt) return "No date";
  const due = new Date(item.dueAt);
  if (due < startOfDay(new Date())) return "Overdue";
  if (sameDay(due, new Date())) return "Today";
  return "Upcoming";
}

function taskBucketRank(item: PersonalTask) { return ["Overdue", "Today", "Upcoming", "No date", "Completed"].indexOf(taskBucket(item)); }
function isOverdue(item: PersonalTask) { return !item.completed && !!item.dueAt && new Date(item.dueAt) < startOfDay(new Date()); }
function dueTime(item: PersonalTask) { return item.dueAt ? Date.parse(item.dueAt) : Number.MAX_SAFE_INTEGER; }
function startOfDay(value: Date) { const date = new Date(value); date.setHours(0, 0, 0, 0); return date; }
function addDays(value: Date, days: number) { const date = new Date(value); date.setDate(date.getDate() + days); return date; }
function sameDay(left: Date, right: Date) { return left.toDateString() === right.toDateString(); }
function unique(values: string[]) { return [...new Set(values.filter(Boolean))].sort((left, right) => left.localeCompare(right)); }
function withCurrent(values: string[], current: string) { return values.includes(current) ? values : [...values, current]; }
function formatDate(value: string) { const date = new Date(value); return date.toLocaleDateString([], { month: "short", day: "numeric" }) + ` · ${date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" })}`; }
function relative(value: string) { const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(value)) / 60_000)); return minutes < 60 ? `${minutes}m` : minutes < 1440 ? `${Math.floor(minutes / 60)}h` : `${Math.floor(minutes / 1440)}d`; }

function useStored(key: string, initial: string): [string, (value: string) => void] {
  const [value, setValue] = useState(() => localStorage.getItem(key) ?? initial);
  return [value, (next) => { setValue(next); localStorage.setItem(key, next); }];
}

async function copyText(value: string) {
  if (navigator.clipboard) { try { await navigator.clipboard.writeText(value); return; } catch { /* Use the LAN-safe fallback below. */ } }
  const textarea = document.createElement("textarea"); textarea.value = value; textarea.style.position = "fixed"; textarea.style.opacity = "0";
  document.body.appendChild(textarea); textarea.select(); document.execCommand("copy"); textarea.remove();
}
