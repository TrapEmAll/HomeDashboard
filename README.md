# HomeDashboard

HomeDashboard is a Windows-friendly homelab dashboard monorepo for monitoring services running on another PC. The MVP includes an ASP.NET Core API, a .NET worker agent, shared contracts, a React + TypeScript web shell, tests, docs, health checks, system-stat placeholders, configurable service cards, RSS news support, and extension points for guarded restart controls.

## Repository layout

```text
src/
  HomeDashboard.Api/        ASP.NET Core API and dashboard aggregation
  HomeDashboard.Agent/      Windows-focused background agent
  HomeDashboard.Contracts/  Shared DTOs and domain contracts
web/                        React + TypeScript dashboard shell
tests/                      .NET tests
docs/                       Architecture and setup notes
```

## Quick start

```powershell
dotnet restore
dotnet test
dotnet run --project src/HomeDashboard.Api
```

The API listens on the default ASP.NET Core ports and exposes:

- `GET /health`
- `GET /api/dashboard`
- `GET /api/services`
- `GET /api/system`
- `GET /api/news`
- `POST /api/agent/snapshot`
- `GET /api/agent/{agentId}/latest`
- `POST /api/services/{id}/restart`

All `/api` endpoints require `X-HomeDashboard-Key`. Dashboard/browser requests use `Security:DashboardApiKey`; agent requests to `/api/agent/*` use `Security:AgentApiKey`.

The web app is a Vite React project:

```powershell
cd web
npm install
npm run dev
```

Node/npm are required for the frontend workflow.

For local web configuration, copy `web/.env.example` to `web/.env` and set `VITE_DASHBOARD_API_KEY` to match the API's `Security:DashboardApiKey`.

The agent posts snapshots to the API:

```powershell
dotnet run --project src/HomeDashboard.Agent
```

Set `Agent:DashboardApiUrl` and `Agent:ApiKey` so the agent can authenticate to the API. The default sample config uses matching local development keys only; change them before using this beyond a local machine.

## Configuration

Edit `src/HomeDashboard.Api/appsettings.json` to define service cards, RSS sources, and API keys. Restart controls are intentionally stubbed behind an explicit endpoint contract until command authorization, allowlisting, and audit logging are added.

## MVP boundaries

- Service and system data are shaped around real contracts, but the API currently uses local/configured providers.
- Agent snapshots are accepted with API-key authentication and stored in memory as the latest status for the configured `DefaultAgentId`.
- RSS support fetches configured feeds and parses common RSS/Atom fields.
- The agent reads configured Windows service states, collects system stat placeholders, and posts snapshots to the API.
- Restart endpoints return `202 Accepted` with a queued status placeholder.
