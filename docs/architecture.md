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
  service cards, native service checks, RSS news, persisted state, browser auth
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

Browser users authenticate with a dashboard password and receive an HttpOnly session cookie. Dashboard API-key access is still available for automation. Agent snapshot and command endpoints require a separate agent API key.

Restart controls use a persisted command queue. The API only queues commands for cards with `RestartEnabled: true`; the agent only executes restarts for matching `Agent:WindowsServices` entries that also have `RestartEnabled: true`.

Next hardening steps:

- An audit log for restart requests.
- Confirmation UX for destructive or disruptive actions.
- Hashed or externalized dashboard password storage.

## Service health

Configured services support:

- A display name and description.
- A quick-link URL.
- An optional health URL.
- A service kind for native checks.
- An optional native API key.
- A restart-enabled flag.

The API maps successful health/native responses to `Online`, 5xx responses to `Degraded`, network failures to `Offline`, and missing health URLs to `Unknown`. Native checks currently cover Plex, *arr apps, qBittorrent, SABnzbd, and Jellyfin. Operations performs bounded parallel *arr queries server-side, normalizes queue, history, health, and missing-media data, and caches the combined snapshot for ten seconds. Broad missing-media searches require an explicit browser confirmation and all accepted commands are audited.

## News

RSS and Atom feeds are configured in `appsettings.json`. The API fetches feeds server-side and returns a small normalized list to the web app.

## Agent behavior

The agent reads configured Windows service statuses, collects disk/memory/sampled CPU stats, posts snapshots to the API with `X-HomeDashboard-Key`, polls for queued commands, and reports command completion.

Next steps:

- Add host-wide CPU counters instead of process CPU sampling.
- Add richer audit and notification hooks.
- Add guided first-run configuration.
