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
  service cards, health checks, RSS news, system stats facade
    |
    | future authenticated channel
    v
Dashboard Agent
  .NET worker on the Windows services PC
  Windows service/system/process collectors

Shared contracts
  DTOs used by API, agent, tests, and eventually generated web clients
```

## Security model

The MVP intentionally keeps restart controls as a contract and queue placeholder. Before real restarts are enabled, the project should add:

- API authentication for browser access.
- Agent authentication for API-to-agent calls.
- An allowlist of restartable services.
- An audit log for restart requests.
- Confirmation UX for destructive or disruptive actions.

## Service health

Configured services support:

- A display name and description.
- A quick-link URL.
- An optional health URL.
- A restart-enabled flag.

The API maps successful health responses to `Online`, 5xx responses to `Degraded`, network failures to `Offline`, and missing health URLs to `Unknown`.

## News

RSS and Atom feeds are configured in `appsettings.json`. The API fetches feeds server-side and returns a small normalized list to the web app.

## Agent roadmap

The initial agent captures the shape of local collection. Next steps:

- Add `ServiceController` integration on Windows.
- Report agent snapshots back to the API.
- Add CPU counters with a platform-specific implementation.
- Add remote restart command handling with signed requests.
- Package as a Windows service.
