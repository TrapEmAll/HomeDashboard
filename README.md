# HomeDashboard

HomeDashboard is a Windows-friendly homelab dashboard monorepo for monitoring services running on another PC. It includes an ASP.NET Core API that serves the React dashboard, a Windows-focused .NET agent, shared contracts, tests, docs, configurable service cards, RSS news, persisted agent history, first-run setup, browser login, alerts, audit history, live updates, and guarded restart commands.

The dashboard command center includes a dark responsive service wall, live health filtering and search, persistent favorites, compact and comfortable density modes, a hideable activity rail, recent health pulse history, richer storage details, and session-expiry recovery. Its operations workspace adds a unified activity timeline, Plex sessions, Sonarr/Radarr calendar, qBittorrent and SABnzbd queues, incident and dependency views, maintenance windows, storage planning, service discovery, browser alerts, opt-in local weather, and configuration backup/restore. A searchable intelligence stream combines configured feeds with a built-in catalog of technology, IT, development, infrastructure, and cybersecurity reporting plus new podcast episodes and Spotify discovery links. Display preferences are stored only in the current browser.

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
- `GET /api/operations`
- `POST /api/downloads/control`
- `GET /api/discovery`
- `GET /api/maintenance`
- `POST /api/maintenance`
- `GET /api/backup`
- `POST /api/backup/restore`
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
- `tools/update-homedashboard.ps1`, an updater that downloads a GitHub branch, preserves local config/data, rebuilds, reinstalls, and restarts the services.
- `tools/open-elevated-update.ps1`, a launcher that opens an elevated PowerShell window in the current source folder and runs the updater.

Update an existing source-based install from GitHub with:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\update-homedashboard.ps1
```

Or launch the updater in an elevated PowerShell window from the current source folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\open-elevated-update.ps1
```

## Configuration

After signing in, open **Dashboard settings** from the gear button to add, edit, or remove service cards and custom RSS/podcast feeds. You can also choose the default agent, enable the built-in intelligence catalog, store integration API keys, and opt services into restart controls. Saved API keys are masked; leave the field blank to keep an existing key. Restart the API service after saving to apply the new configuration.

In **Dashboard settings > Custom feeds**, use **Choose OPML** to import subscriptions exported by Feedly, Inoreader, FreshRSS, Thunderbird, and other RSS readers. OPML folders become feed categories, podcast folders are recognized automatically, and duplicate or invalid feed URLs are skipped. Review the imported rows and select **Save changes** to persist them.

The settings editor supports Plex, Sonarr, Radarr, Lidarr, Readarr, Prowlarr, Bazarr, qBittorrent, SABnzbd, Jellyfin, game servers, file shares, and generic HTTP checks. Advanced installation and security values remain available in `appsettings.Local.json`; see [docs/configuration.md](docs/configuration.md).

Keyboard controls are available while you are not typing in a field: `/` focuses service search, `R` refreshes, `S` opens settings, and `Escape` closes settings.

## Build troubleshooting

The repository includes an isolated API build script, so restore and build do not depend on the current Windows user's roaming NuGet settings. To verify the API by itself, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-api.ps1
```

If this reports that no compatible SDK is installed, install the .NET 8 SDK or a newer SDK and rerun the command. The complete Windows package additionally requires Node.js for the React production build.

Restart controls require browser confirmation, queue commands for `Dashboard:DefaultAgentId`, and write audit events for queued, rejected, and completed commands. The agent only executes a restart when the matching `Agent:WindowsServices` entry exists and has `RestartEnabled: true`.

## MVP boundaries

- Service and system data are shaped around shared contracts. The API merges configured app checks with the latest persisted agent snapshot.
- Agent snapshots are accepted with API-key authentication, persisted to disk, and summarized with rolling history for the configured `DefaultAgentId`.
- First-class service checks report status and metric chips for Plex, *arr apps, qBittorrent, SABnzbd, and Jellyfin when their URLs/API keys are configured.
- RSS support fetches configured and recommended feeds concurrently, parses common RSS/Atom fields, limits each source for variety, caches results, and distinguishes articles from podcast episodes.
- The agent reads configured Windows service states, collects disk/memory/Windows host CPU stats, posts snapshots to the API, polls restart commands, and reports command completion.
- Alerts surface offline/degraded services, stale agents, and nearly-full disks; the browser receives live dashboard snapshots over server-sent events.
