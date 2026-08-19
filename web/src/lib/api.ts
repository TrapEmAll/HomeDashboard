import type { DashboardSnapshot } from "../types/dashboard";

const apiBaseUrl = import.meta.env.VITE_API_BASE_URL ?? "";
const apiKey = import.meta.env.VITE_DASHBOARD_API_KEY ?? "change-me-dashboard-key";

const apiHeaders = {
  "X-HomeDashboard-Key": apiKey
};

export async function getDashboard(): Promise<DashboardSnapshot> {
  const response = await fetch(`${apiBaseUrl}/api/dashboard`, { headers: apiHeaders });
  if (!response.ok) {
    throw new Error(`Dashboard request failed with ${response.status}`);
  }

  return response.json() as Promise<DashboardSnapshot>;
}

export async function requestRestart(serviceId: string): Promise<void> {
  const response = await fetch(`${apiBaseUrl}/api/services/${serviceId}/restart`, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...apiHeaders },
    body: JSON.stringify({ requestedBy: "dashboard", reason: "Manual dashboard action" })
  });

  if (!response.ok && response.status !== 202) {
    throw new Error(`Restart request failed with ${response.status}`);
  }
}
