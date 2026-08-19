import { Newspaper } from "lucide-react";
import type { NewsItem } from "../types/dashboard";

interface Props {
  items: NewsItem[];
}

export function NewsPanel({ items }: Props) {
  return (
    <section className="panel">
      <div className="section-heading">
        <h2>News</h2>
        <span>{items.length} latest</span>
      </div>
      <div className="news-list">
        {items.length === 0 ? (
          <div className="empty-state">
            <Newspaper size={20} />
            <span>No feed items available.</span>
          </div>
        ) : (
          items.map((item) => (
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
