import { useEffect, useMemo, useState } from "react";
import { Bookmark, Check, Clipboard, ExternalLink, Eye, EyeOff, Grid2X2, Headphones, List, Newspaper, RotateCcw, Search, Share2, X } from "lucide-react";
import type { NewsContentKind, NewsItem } from "../types/dashboard";

interface Props { items: NewsItem[]; }
type ContentFilter = "All" | NewsContentKind;
type ReadingFilter = "All" | "Unread" | "Saved";
type AgeFilter = "All" | "Day" | "Week" | "Month";
type SortOrder = "Newest" | "Oldest" | "Source" | "Title";
type ViewMode = "grid" | "list";
const pageSize = 16;

export function NewsPanel({ items }: Props) {
  const newsItems = Array.isArray(items) ? items : [];
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState<ContentFilter>("All");
  const [category, setCategory] = useState("All");
  const [source, setSource] = useState("All");
  const [age, setAge] = useState<AgeFilter>("All");
  const [sort, setSort] = useState<SortOrder>("Newest");
  const [reading, setReading] = useState<ReadingFilter>("All");
  const [showHidden, setShowHidden] = useState(false);
  const [view, setView] = useState<ViewMode>(() => localStorage.getItem("homedashboard-content-view") === "list" ? "list" : "grid");
  const [compact, setCompact] = useState(() => localStorage.getItem("homedashboard-content-density") === "compact");
  const [readItems, setReadItems] = useState<Set<string>>(() => readStoredSet("homedashboard-content-read"));
  const [savedItems, setSavedItems] = useState<Set<string>>(() => readStoredSet("homedashboard-content-saved"));
  const [hiddenItems, setHiddenItems] = useState<Set<string>>(() => readStoredSet("homedashboard-content-hidden"));
  const [visibleCount, setVisibleCount] = useState(pageSize);
  const [playbackRate, setPlaybackRate] = useState(1);

  function updatePlaybackRate(rate: number) {
    setPlaybackRate(rate);
    document.querySelectorAll<HTMLAudioElement>(".podcast-player audio").forEach((audio) => {
      audio.playbackRate = rate;
    });
  }
  const [notice, setNotice] = useState<string | null>(null);

  const categories = useMemo(() => uniqueValues(newsItems.map((item) => item.category)), [newsItems]);
  const sources = useMemo(() => uniqueValues(newsItems.map((item) => item.source)), [newsItems]);
  const filteredItems = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    const cutoff = ageCutoff(age);
    const next = newsItems.filter((item) => {
      const id = itemKey(item);
      const published = item.publishedAt ? new Date(item.publishedAt).getTime() : 0;
      const matchesQuery = !needle || [item.source, item.title, item.summary, item.category].some((value) => value?.toLocaleLowerCase().includes(needle));
      return (kind === "All" || item.kind === kind)
        && (category === "All" || item.category === category)
        && (source === "All" || item.source === source)
        && (cutoff === 0 || published >= cutoff)
        && (reading !== "Unread" || !readItems.has(id))
        && (reading !== "Saved" || savedItems.has(id))
        && (showHidden ? hiddenItems.has(id) : !hiddenItems.has(id))
        && matchesQuery;
    });
    return next.sort((left, right) => compareItems(left, right, sort));
  }, [age, category, hiddenItems, kind, newsItems, query, readItems, reading, savedItems, showHidden, sort, source]);
  const visibleItems = filteredItems.slice(0, visibleCount);

  useEffect(() => setVisibleCount(pageSize), [age, category, kind, query, reading, showHidden, sort, source]);

  function updateStoredSet(key: string, setter: (value: Set<string>) => void, current: Set<string>, id: string, enabled?: boolean) {
    const next = new Set(current);
    const shouldEnable = enabled ?? !next.has(id);
    if (shouldEnable) next.add(id); else next.delete(id);
    localStorage.setItem(key, JSON.stringify([...next]));
    setter(next);
  }
  function markRead(item: NewsItem, enabled = true) { updateStoredSet("homedashboard-content-read", setReadItems, readItems, itemKey(item), enabled); }
  function toggleSaved(item: NewsItem) { updateStoredSet("homedashboard-content-saved", setSavedItems, savedItems, itemKey(item)); }
  function toggleHidden(item: NewsItem) { updateStoredSet("homedashboard-content-hidden", setHiddenItems, hiddenItems, itemKey(item)); }
  function markVisibleRead() {
    const next = new Set(readItems);
    filteredItems.forEach((item) => next.add(itemKey(item)));
    localStorage.setItem("homedashboard-content-read", JSON.stringify([...next]));
    setReadItems(next);
  }
  function clearFilters() { setQuery(""); setKind("All"); setCategory("All"); setSource("All"); setAge("All"); setSort("Newest"); setReading("All"); setShowHidden(false); }
  function changeView(next: ViewMode) { setView(next); localStorage.setItem("homedashboard-content-view", next); }
  function toggleDensity() { const next = !compact; setCompact(next); localStorage.setItem("homedashboard-content-density", next ? "compact" : "comfortable"); }
  function flashNotice(message: string) { setNotice(message); window.setTimeout(() => setNotice(null), 2200); }
  async function share(item: NewsItem) {
    if (!item.url) return;
    try {
      if (navigator.share) await navigator.share({ title: item.title, text: item.source, url: item.url });
      else { await navigator.clipboard.writeText(item.url); flashNotice("Link copied"); }
    } catch (error) { if ((error as DOMException).name !== "AbortError") flashNotice("Sharing is unavailable here"); }
  }
  async function copy(item: NewsItem) {
    if (!item.url) return;
    try { await navigator.clipboard.writeText(item.url); flashNotice("Link copied"); } catch { flashNotice("Clipboard access is unavailable"); }
  }

  return <section className={`content-hub ${compact ? "compact" : ""}`} id="content">
    <div className="content-heading"><div><span className="section-kicker">Personal feed reader</span><h2>Intelligence stream</h2><p>Articles and podcasts with persistent reading state stored only in this browser.</p></div><div className="content-heading-stats"><span>{filteredItems.length} shown</span><span>{savedItems.size} saved</span><span>{hiddenItems.size} hidden</span></div></div>
    <div className="content-toolbar">
      <label className="content-search"><Search size={17} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search headlines, shows, topics, or sources" />{query ? <button type="button" onClick={() => setQuery("")} title="Clear content search"><X size={16} /></button> : null}</label>
      <div className="content-kind-filter" role="group" aria-label="Content type">{(["All", "Article", "Podcast"] as const).map((value) => <button className={kind === value ? "active" : ""} type="button" onClick={() => setKind(value)} key={value}>{value === "Podcast" ? <Headphones size={14} /> : value === "Article" ? <Newspaper size={14} /> : null}{value}<span>{value === "All" ? newsItems.length : newsItems.filter((item) => item.kind === value).length}</span></button>)}</div>
      <div className="content-view-controls" role="group" aria-label="Content layout"><button className={view === "grid" ? "active" : ""} type="button" title="Grid view" onClick={() => changeView("grid")}><Grid2X2 size={15} /></button><button className={view === "list" ? "active" : ""} type="button" title="List view" onClick={() => changeView("list")}><List size={15} /></button><button className={compact ? "active" : ""} type="button" title="Compact cards" onClick={toggleDensity}><Eye size={15} /></button></div>
    </div>
    <div className="content-filter-row">
      <FilterSelect label="Topic" value={category} values={["All", ...categories]} onChange={setCategory} />
      <FilterSelect label="Source" value={source} values={["All", ...sources]} onChange={setSource} />
      <FilterSelect label="Age" value={age} values={["All", "Day", "Week", "Month"]} onChange={(value) => setAge(value as AgeFilter)} />
      <FilterSelect label="Sort" value={sort} values={["Newest", "Oldest", "Source", "Title"]} onChange={(value) => setSort(value as SortOrder)} />
      <div className="reading-filter" role="group" aria-label="Reading state">{(["All", "Unread", "Saved"] as const).map((value) => <button className={reading === value ? "active" : ""} type="button" key={value} onClick={() => setReading(value)}>{value}</button>)}</div>
      <button className={`hidden-toggle ${showHidden ? "active" : ""}`} type="button" onClick={() => setShowHidden((value) => !value)}><EyeOff size={14} />{showHidden ? "Hidden only" : "Show hidden"}</button>
      <button className="text-tool" type="button" onClick={markVisibleRead}><Check size={14} />Mark shown read</button>
      <button className="icon-tool" type="button" title="Reset content filters" onClick={clearFilters}><RotateCcw size={15} /></button>
    </div>
    {notice ? <div className="content-notice">{notice}</div> : null}
    {filteredItems.length === 0 ? <div className="content-empty"><Search size={20} /> No stories or episodes match these filters.</div> : <>
      <div className={`content-grid ${view}`}>{visibleItems.map((item) => {
        const id = itemKey(item); const isRead = readItems.has(id); const isSaved = savedItems.has(id);
        return <article className={`content-item ${item.kind.toLocaleLowerCase()} ${isRead ? "read" : ""}`} key={id}>
          <div className="content-item-visual">{item.imageUrl ? <img src={item.imageUrl} alt="" loading="lazy" referrerPolicy="no-referrer" /> : <div className="content-item-icon">{item.kind === "Podcast" ? <Headphones size={20} /> : <Newspaper size={20} />}</div>}</div>
          <div className="content-item-body"><div className="content-meta"><span>{item.source}</span><b>{item.category}</b>{item.duration ? <small>{item.duration}</small> : null}{item.publishedAt ? <time title={new Date(item.publishedAt).toLocaleString()}>{formatRelativeTime(item.publishedAt)}</time> : null}</div><h3>{item.title}</h3>{item.summary ? <p>{stripMarkup(item.summary)}</p> : null}
            {item.kind === "Podcast" && item.mediaUrl ? <div className="podcast-player"><audio controls preload="none" src={item.mediaUrl} onPlay={() => markRead(item)} onLoadedMetadata={(event) => { event.currentTarget.playbackRate = playbackRate; }} /><select aria-label="Playback speed" value={playbackRate} onChange={(event) => updatePlaybackRate(Number(event.target.value))}>{[0.75, 1, 1.25, 1.5, 2].map((rate) => <option value={rate} key={rate}>{rate}x</option>)}</select></div> : null}
            <div className="content-actions">{item.url ? <a href={item.url} target="_blank" rel="noreferrer" onClick={() => markRead(item)}>{item.kind === "Podcast" ? "Episode page" : "Read story"}<ExternalLink size={13} /></a> : null}{item.kind === "Podcast" && item.providerUrl ? <a className="spotify-link" href={item.providerUrl} target="_blank" rel="noreferrer" onClick={() => markRead(item)}><Headphones size={13} />Provider</a> : null}<button className={isSaved ? "active" : ""} type="button" title={isSaved ? "Remove bookmark" : "Save item"} onClick={() => toggleSaved(item)}><Bookmark size={14} /></button><button className={isRead ? "active" : ""} type="button" title={isRead ? "Mark unread" : "Mark read"} onClick={() => markRead(item, !isRead)}><Check size={14} /></button>{item.url ? <button type="button" title="Copy link" onClick={() => void copy(item)}><Clipboard size={14} /></button> : null}{item.url ? <button type="button" title="Share" onClick={() => void share(item)}><Share2 size={14} /></button> : null}<button type="button" title={showHidden ? "Restore item" : "Hide item"} onClick={() => toggleHidden(item)}>{showHidden ? <Eye size={14} /> : <EyeOff size={14} />}</button></div>
          </div>
        </article>;
      })}</div>
      {visibleCount < filteredItems.length ? <button className="content-load-more" type="button" onClick={() => setVisibleCount((value) => value + pageSize)}>Load {Math.min(pageSize, filteredItems.length - visibleCount)} more</button> : null}
    </>}
  </section>;
}

function FilterSelect({ label, value, values, onChange }: { label: string; value: string; values: string[]; onChange: (value: string) => void }) { return <label className="category-select"><span>{label}</span><select aria-label={label} value={value} onChange={(event) => onChange(event.target.value)}>{values.map((item) => <option key={item}>{item}</option>)}</select></label>; }
function itemKey(item: NewsItem): string { return item.url ?? `${item.source}|${item.title}|${item.publishedAt ?? ""}`; }
function uniqueValues(values: Array<string | null | undefined>): string[] { return [...new Set(values.filter((value): value is string => Boolean(value)))].sort((a, b) => a.localeCompare(b)); }
function ageCutoff(age: AgeFilter): number { const days = age === "Day" ? 1 : age === "Week" ? 7 : age === "Month" ? 30 : 0; return days ? Date.now() - days * 86_400_000 : 0; }
function compareItems(left: NewsItem, right: NewsItem, sort: SortOrder): number { if (sort === "Source") return left.source.localeCompare(right.source) || left.title.localeCompare(right.title); if (sort === "Title") return left.title.localeCompare(right.title); const leftDate = left.publishedAt ? new Date(left.publishedAt).getTime() : 0; const rightDate = right.publishedAt ? new Date(right.publishedAt).getTime() : 0; return sort === "Oldest" ? leftDate - rightDate : rightDate - leftDate; }
function readStoredSet(key: string): Set<string> { try { const values = JSON.parse(localStorage.getItem(key) ?? "[]"); return new Set(Array.isArray(values) ? values.filter((value): value is string => typeof value === "string") : []); } catch { return new Set(); } }
function formatRelativeTime(value: string): string { const elapsedMinutes = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 60_000)); if (elapsedMinutes < 60) return `${elapsedMinutes}m`; const hours = Math.floor(elapsedMinutes / 60); return hours < 24 ? `${hours}h` : `${Math.floor(hours / 24)}d`; }
function stripMarkup(value: string): string { return decodeEntities(value.replace(/<[^>]*>/g, " ")).replace(/\s+/g, " ").trim().slice(0, 260); }
function decodeEntities(value: string): string { return value.replace(/&#x([0-9a-f]+);/gi, (_, code: string) => String.fromCodePoint(Number.parseInt(code, 16))).replace(/&#(\d+);/g, (_, code: string) => String.fromCodePoint(Number.parseInt(code, 10))).replace(/&nbsp;/g, " ").replace(/&amp;/g, "&").replace(/&quot;/g, '"').replace(/&apos;|&#39;/g, "'").replace(/&lt;/g, "<").replace(/&gt;/g, ">"); }
