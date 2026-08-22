import { Newspaper } from "lucide-react";
import type { NewsItem } from "../types/dashboard";

interface Props {
  items: NewsItem[];
}

export function NewsPanel({ items }: Props) {
  const newsItems = Array.isArray(items) ? items : [];
  return (
    <section className="panel">
      <div className="section-heading">
        <h2>News</h2>
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
              <span>{item.source}</span>
              <strong>{item.title}</strong>
              {item.publishedAt ? <time>{new Date(item.publishedAt).toLocaleString()}</time> : null}
            </a>
          ))
        )}
      </div>
    </section>
  );
}
