# HomeDashboard

HomeDashboard is a Windows-friendly homelab dashboard monorepo for monitoring services running on another PC. It includes an ASP.NET Core API that serves the React dashboard, a Windows-focused .NET agent, shared contracts, tests, docs, configurable service cards, RSS news, persisted agent history, first-run setup, browser login, alerts, audit history, live updates, and guarded restart commands.

## Repository layout

```text
src/
  HomeDashboard.Api/        ASP.NET Core API and dashboard aggregation
  HomeDashboard.Agent/      Windows-focused background agent
  HomeDashboard.Contracts/  Shared DTOs and domain contracts
web/                        React + TypeScript dashboard shell
tests/                      .NET tests
docs/                       Architecture and setup notes
tools/                      Windows publishing and service installer helpers
```

## Quick start

```powershell
dotnet restore
dotnet test
dotnet run --project src/HomeDashboard.Api --urls http://localhost:5000
```

Open `http://localhost:5000` and sign in with `Security:DashboardPassword` from `src/HomeDashboard.Api/appsettings.json`.

The API exposes:

- `GET /health`
- `POST /auth/login`
- `POST /auth/logout`
- `GET /auth/session`
- `GET /setup/status`
- `POST /setup`
- `GET /api/dashboard`
- `GET /api/events`
- `GET /api/services`
- `GET /api/system`
- `GET /api/news`
- `GET /api/audit`
- `GET /api/commands`
- `POST /api/agent/snapshot`
- `GET /api/agents`
- `GET /api/agent/{agentId}/latest`
- `GET /api/agent/{agentId}/history`
- `GET /api/agent/{agentId}/commands/next`
- `POST /api/agent/{agentId}/commands/{commandId}/complete`
- `POST /api/services/{id}/restart`

Dashboard browser requests use an HttpOnly session cookie after login. Dashboard API-key access is still supported with `X-HomeDashboard-Key: Security:DashboardApiKey`. Agent snapshot and command endpoints require `Security:AgentApiKey`.

The web app is a Vite React project:

```powershell
cd web
npm install
npm run dev
```

Node/npm are required for the frontend workflow.

For local web configuration, copy `web/.env.example` to `web/.env` and set `VITE_API_BASE_URL` if the API is not running on the same origin.

The agent posts snapshots to the API:

```powershell
dotnet run --project src/HomeDashboard.Agent
```

Set `Agent:DashboardApiUrl` and `Agent:ApiKey` so the agent can authenticate to the API. The default sample config uses matching local development keys only; change them before using this beyond a local machine.

## Windows package

Build the runnable Windows package:

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-windows.ps1
```

The package is written to `outputs/HomeDashboard-Windows.zip`. It contains:

- `api/HomeDashboard.Api.exe`, which serves the API and bundled web dashboard.
- `agent/HomeDashboard.Agent.exe`, which reports Windows service/system state and runs queued restart commands.
- `tools/install-api-service.ps1` and `tools/install-agent-service.ps1` for optional Windows service installation.
- `tools/install-homedashboard.ps1`, a combined installer wrapper for the packaged API and agent services.

## Configuration

Edit `src/HomeDashboard.Api/appsettings.json` to define browser auth, service cards, RSS sources, API keys, persisted state location, and service integrations. Supported service kinds include Plex, Sonarr, Radarr, Lidarr, Readarr, Prowlarr, Bazarr, qBittorrent, SABnzbd, Jellyfin, game servers, file shares, and generic HTTP checks.

Restart controls require browser confirmation, queue commands for `Dashboard:DefaultAgentId`, and write audit events for queued, rejected, and completed commands. The agent only executes a restart when the matching `Agent:WindowsServices` entry exists and has `RestartEnabled: true`.

## MVP boundaries

- Service and system data are shaped around shared contracts. The API merges configured app checks with the latest persisted agent snapshot.
- Agent snapshots are accepted with API-key authentication, persisted to disk, and summarized with rolling history for the configured `DefaultAgentId`.
- First-class service checks report status and metric chips for Plex, *arr apps, qBittorrent, SABnzbd, and Jellyfin when their URLs/API keys are configured.
- RSS support fetches configured feeds and parses common RSS/Atom fields.
- The agent reads configured Windows service states, collects disk/memory/Windows host CPU stats, posts snapshots to the API, polls restart commands, and reports command completion.
- Alerts surface offline/degraded services, stale agents, and nearly-full disks; the browser receives live dashboard snapshots over server-sent events.
