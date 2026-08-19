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
- `POST /api/services/{id}/restart`

The web app is a Vite React project:

```powershell
cd web
npm install
npm run dev
```

Node/npm are required for the frontend workflow.

## Configuration

Edit `src/HomeDashboard.Api/appsettings.json` to define service cards and RSS sources. Restart controls are intentionally stubbed behind an explicit endpoint contract until agent authentication and authorization are added.

## MVP boundaries

- Service and system data are shaped around real contracts, but the API currently uses local/configured providers.
- RSS support fetches configured feeds and parses common RSS/Atom fields.
- The agent exposes the collection model for Windows services/system stats but does not yet accept remote commands.
- Restart endpoints return `202 Accepted` with a queued status placeholder.
