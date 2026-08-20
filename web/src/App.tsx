import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { Activity, LogOut, RefreshCw, ShieldCheck } from "lucide-react";
import { getDashboard, getSession, login, logout, requestRestart } from "./lib/api";
import { NewsPanel } from "./components/NewsPanel";
import { ServiceGrid } from "./components/ServiceGrid";
import { SystemPanel } from "./components/SystemPanel";
import type { DashboardSnapshot } from "./types/dashboard";
import "./styles.css";

export function App() {
  const [snapshot, setSnapshot] = useState<DashboardSnapshot | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [authenticated, setAuthenticated] = useState(false);
  const [password, setPassword] = useState("");
  const [signingIn, setSigningIn] = useState(false);

  async function load() {
    setLoading(true);
    setError(null);
    try {
      setSnapshot(await getDashboard());
      setAuthenticated(true);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : "Dashboard request failed.");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
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

    const handle = window.setInterval(() => void load(), 30_000);
    return () => window.clearInterval(handle);
  }, [authenticated]);

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
    await requestRestart(serviceId);
    await load();
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

  return (
    <main className="app-shell">
      <header className="topbar">
        <div>
          <h1>HomeDashboard</h1>
          <p>Services, host health, and news in one place.</p>
        </div>
        <div className="topbar-actions">
          <button className="refresh-button" type="button" onClick={() => void load()} disabled={loading}>
            <RefreshCw size={18} />
            Refresh
          </button>
          <button className="icon-button" type="button" onClick={() => void signOut()} title="Sign out">
            <LogOut size={18} />
          </button>
        </div>
      </header>

      {error ? <div className="error-banner">{error}</div> : null}

      {snapshot ? (
        <div className="dashboard-layout">
          <div className="main-column">
            <ServiceGrid services={snapshot.services} onRestart={(serviceId) => void restart(serviceId)} />
          </div>
          <aside className="side-column">
            <section className="panel">
              <div className="section-heading">
                <h2>Agents</h2>
                <span>{snapshot.agents.length} connected</span>
              </div>
              <div className="agent-list">
                {snapshot.agents.length > 0 ? (
                  snapshot.agents.map((agent) => (
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
