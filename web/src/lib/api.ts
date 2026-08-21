import type { DashboardSnapshot, SetupRequest, SetupStatus } from "../types/dashboard";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "";

export interface AuthSession {
  isAuthenticated: boolean;
  expiresAt?: string | null;
}

async function readJson<T>(response: Response): Promise<T> {
  if (!response.ok) {
    throw new Error(`Request failed with ${response.status}`);
  }

  return response.json() as Promise<T>;
}

export async function getSession(): Promise<AuthSession> {
  const response = await fetch(`${apiBaseUrl}/auth/session`, { credentials: "include" });
  return readJson<AuthSession>(response);
}

export async function login(password: string): Promise<AuthSession> {
  const response = await fetch(`${apiBaseUrl}/auth/login`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ password })
  });

  return readJson<AuthSession>(response);
}

export async function logout(): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/auth/logout`, {
    method: "POST",
    credentials: "include"
  });
  if (!response.ok && response.status !== 204) {
    throw new Error(`Logout failed with ${response.status}`);
  }
}

export async function getSetupStatus(): Promise<SetupStatus> {
  const response = await fetch(`${apiBaseUrl}/setup/status`, { credentials: "include" });
  return readJson<SetupStatus>(response);
}

export async function saveSetup(request: SetupRequest): Promise<SetupStatus> {
  const response = await fetch(`${apiBaseUrl}/setup`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request)
  });

  return readJson<SetupStatus>(response);
}

export async function getDashboard(): Promise<DashboardSnapshot> {
  const response = await fetch(`${apiBaseUrl}/api/dashboard`, { credentials: "include" });
  if (!response.ok) {
    throw new Error(`Dashboard request failed with ${response.status}`);
  }

  return response.json() as Promise<DashboardSnapshot>;
}

export async function requestRestart(serviceId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/services/${serviceId}/restart`, {
    method: "POST",
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ requestedBy: "dashboard", reason: "Manual dashboard action", confirmed: true })
  });

  if (!response.ok && response.status !== 202) {
    throw new Error(`Restart request failed with ${response.status}`);
  }
}

export function dashboardEventsUrl(): string {
  return `${apiBaseUrl}/api/events`;
}
