import { ExternalLink, Newspaper } from "lucide-react";
import type { NewsItem } from "../types/dashboard";

interface Props {
  items: NewsItem[];
}

export function NewsPanel({ items }: Props) {
  const newsItems = Array.isArray(items) ? items : [];
  return (
    <section className="panel news-panel">
      <div className="section-heading">
        <div>
          <span className="section-kicker">Feeds</span>
          <h2>News</h2>
        </div>
        <span>{newsItems.length} latest</span>
      </div>
      <div className="news-list">
        {newsItems.length === 0 ? (
          <div className="empty-state">
            <Newspaper size={20} />
            <span>No feed items available.</span>
          </div>
        ) : (
          newsItems.map((item) => (
            <a className="news-item" href={item.url ?? "#"} key={`${item.source}-${item.title}`} target="_blank" rel="noreferrer">
              <div className="news-meta"><span>{item.source}</span>{item.publishedAt ? <time title={new Date(item.publishedAt).toLocaleString()}>{formatRelativeTime(item.publishedAt)}</time> : null}</div>
              <strong>{item.title}</strong>
              {item.summary ? <p>{stripMarkup(item.summary)}</p> : null}
              <ExternalLink className="news-link-icon" size={14} />
            </a>
          ))
        )}
      </div>
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
  return value.replace(/<[^>]*>/g, " ").replace(/\s+/g, " ").trim().slice(0, 150);
}
