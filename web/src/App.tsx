import { useEffect, useState } from "react";
import { Activity, RefreshCw } from "lucide-react";
import { getDashboard, requestRestart } from "./lib/api";
import { NewsPanel } from "./components/NewsPanel";
import { ServiceGrid } from "./components/ServiceGrid";
import { SystemPanel } from "./components/SystemPanel";
import type { DashboardSnapshot } from "./types/dashboard";
import "./styles.css";

export function App() {
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setSnapshot(await getDashboard());
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Dashboard request failed.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void load();
    const handle = window.setInterval(() => void load(), 30_000);
    return () => window.clearInterval(handle);
  }, []);

  async function restart(serviceId: string) {
    await requestRestart(serviceId);
    await load();
  }

  return (
    <main className="app-shell">
      <header className="topbar">
        <div>
          <h1>HomeDashboard</h1>
          <p>Services, host health, and news in one place.</p>
        </div>
        <button className="refresh-button" type="button" onClick={() => void load()} disabled={loading}>
          <RefreshCw size={18} />
          Refresh
        </button>
      </header>

      {error ? <div className="error-banner">{error}</div> : null}

      {snapshot ? (
        <div className="dashboard-layout">
          <div className="main-column">
            <ServiceGrid services={snapshot.services} onRestart={(serviceId) => void restart(serviceId)} />
          </div>
          <aside className="side-column">
            <SystemPanel system={snapshot.system} />
            <NewsPanel items={snapshot.news} />
          </aside>
        </div>
      ) : (
        <div className="loading-panel">
          <Activity size={22} />
          <span>{loading ? "Loading dashboard..." : "Dashboard unavailable."}</span>
        </div>
      )}
    </main>
  );
}
