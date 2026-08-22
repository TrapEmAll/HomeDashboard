import { useMemo, useState } from "react";
import { ExternalLink, Headphones, Newspaper, Search, X } from "lucide-react";
import type { NewsContentKind, NewsItem } from "../types/dashboard";

interface Props {
  items: NewsItem[];
}

type ContentFilter = "All" | NewsContentKind;

export function NewsPanel({ items }: Props) {
  const newsItems = Array.isArray(items) ? items : [];
  const [query, setQuery] = useState("");
  const [kind, setKind] = useState<ContentFilter>("All");
  const [category, setCategory] = useState("All");
  const categories = useMemo(() => ["All", ...new Set(newsItems.map((item) => item.category).filter(Boolean))], [newsItems]);
  const filteredItems = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return newsItems.filter((item) => {
      const matchesKind = kind === "All" || item.kind === kind;
      const matchesCategory = category === "All" || item.category === category;
      const matchesQuery = needle.length === 0 || [item.source, item.title, item.summary, item.category]
        .some((value) => value?.toLocaleLowerCase().includes(needle));
      return matchesKind && matchesCategory && matchesQuery;
    });
  }, [category, kind, newsItems, query]);

  return (
    <section className="content-hub" id="content">
      <div className="content-heading">
        <div>
          <span className="section-kicker">Tech, IT, security, and podcasts</span>
          <h2>Intelligence stream</h2>
          <p>Fresh reporting, research, write-ups, and new episodes from the built-in feed catalog.</p>
        </div>
        <span>{filteredItems.length} items</span>
      </div>

      <div className="content-toolbar">
        <label className="content-search">
          <Search size={17} />
          <input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search headlines, shows, topics, or sources" />
          {query ? <button type="button" onClick={() => setQuery("")} title="Clear content search"><X size={16} /></button> : null}
        </label>
        <div className="content-kind-filter" role="group" aria-label="Content type">
          {(["All", "Article", "Podcast"] as const).map((value) => (
            <button className={kind === value ? "active" : ""} type="button" onClick={() => setKind(value)} key={value}>
              {value === "Podcast" ? <Headphones size={14} /> : value === "Article" ? <Newspaper size={14} /> : null}
              {value}
              <span>{value === "All" ? newsItems.length : newsItems.filter((item) => item.kind === value).length}</span>
            </button>
          ))}
        </div>
        <label className="category-select">
          <span>Topic</span>
          <select aria-label="Topic" value={category} onChange={(event) => setCategory(event.target.value)}>
            {categories.map((value) => <option key={value}>{value}</option>)}
          </select>
        </label>
      </div>

      {filteredItems.length === 0 ? (
        <div className="content-empty"><Search size={20} /> No stories or episodes match this search.</div>
      ) : (
        <div className="content-grid">
          {filteredItems.map((item) => (
            <article className={`content-item ${item.kind.toLocaleLowerCase()}`} key={`${item.source}-${item.title}-${item.publishedAt ?? ""}`}>
              <div className="content-item-icon">{item.kind === "Podcast" ? <Headphones size={20} /> : <Newspaper size={20} />}</div>
              <div className="content-item-body">
                <div className="content-meta">
                  <span>{item.source}</span>
                  <b>{item.category}</b>
                  {item.publishedAt ? <time title={new Date(item.publishedAt).toLocaleString()}>{formatRelativeTime(item.publishedAt)}</time> : null}
                </div>
                <h3>{item.title}</h3>
                {item.summary ? <p>{stripMarkup(item.summary)}</p> : null}
                <div className="content-actions">
                  {item.url ? <a href={item.url} target="_blank" rel="noreferrer">{item.kind === "Podcast" ? "Play episode" : "Read story"}<ExternalLink size={13} /></a> : null}
                  {item.kind === "Podcast" && item.providerUrl ? <a className="spotify-link" href={item.providerUrl} target="_blank" rel="noreferrer"><Headphones size={13} />Find on Spotify</a> : null}
                </div>
              </div>
            </article>
          ))}
        </div>
      )}
    </section>
  );
}

function formatRelativeTime(value: string): string {
  const elapsedMinutes = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 60_000));
  if (elapsedMinutes < 60) return `${elapsedMinutes}m`;
  const hours = Math.floor(elapsedMinutes / 60);
  return hours < 24 ? `${hours}h` : `${Math.floor(hours / 24)}d`;
}

function stripMarkup(value: string): string {
  return decodeEntities(value.replace(/<[^>]*>/g, " ")).replace(/\s+/g, " ").trim().slice(0, 220);
}

function decodeEntities(value: string): string {
  return value
    .replace(/&#x([0-9a-f]+);/gi, (_, code: string) => String.fromCodePoint(Number.parseInt(code, 16)))
    .replace(/&#(\d+);/g, (_, code: string) => String.fromCodePoint(Number.parseInt(code, 10)))
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&quot;/g, '"')
    .replace(/&apos;|&#39;/g, "'")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">");
}
