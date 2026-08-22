import { Component, type ErrorInfo, type ReactNode } from "react";
import { AlertTriangle, RefreshCw } from "lucide-react";

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
}

export class ErrorBoundary extends Component<Props, State> {
  public state: State = { error: null };

  public static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  public componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error("Dashboard render failed", error, errorInfo);
  }

  public render() {
    if (this.state.error) {
      return (
        <main className="auth-shell">
          <section className="login-panel">
            <div className="login-mark">
              <AlertTriangle size={28} />
            </div>
            <h1>Dashboard hit a display error</h1>
            <p>{this.state.error.message}</p>
            <button type="button" onClick={() => window.location.reload()}>
              <RefreshCw size={18} />
              Reload
            </button>
          </section>
        </main>
      );
    }

    return this.props.children;
  }
}
