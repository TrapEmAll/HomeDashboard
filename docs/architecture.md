# Architecture

HomeDashboard is split into four deployable or reusable areas:

```text
Browser
  React + TypeScript dashboard
    |
    | HTTP
    v
Dashboard API
  ASP.NET Core minimal API
  service cards, native service checks, RSS news, system stats facade, API-key auth
    |
    | X-HomeDashboard-Key
    v
Dashboard Agent
  .NET worker on the Windows services PC
  Windows service status and system collectors

Shared contracts
  DTOs used by API, agent, tests, and eventually generated web clients
```

## Security model

The MVP requires an API key for all `/api` routes. Browser/dashboard calls use the dashboard key; agent calls to `/api/agent/*` use a separate agent key.

Restart controls remain a contract and queue placeholder. Before real restarts are enabled, the project should add:

- An allowlist of restartable services.
- An audit log for restart requests.
- Confirmation UX for destructive or disruptive actions.

## Service health

Configured services support:

- A display name and description.
- A quick-link URL.
- An optional health URL.
- A service kind for native checks.
- An optional native API key.
- A restart-enabled flag.

The API maps successful health/native responses to `Online`, 5xx responses to `Degraded`, network failures to `Offline`, and missing health URLs to `Unknown`. Native checks currently cover Plex, *arr apps, qBittorrent, SABnzbd, and Jellyfin.

## News

RSS and Atom feeds are configured in `appsettings.json`. The API fetches feeds server-side and returns a small normalized list to the web app.

## Agent roadmap

The initial agent reads configured Windows service statuses and posts snapshots to the API with `X-HomeDashboard-Key`. Next steps:

- Add CPU counters with a platform-specific implementation.
- Add remote restart command handling with signed requests.
- Package as a Windows service.
