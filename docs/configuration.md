# Configuration

## API service cards

`src/HomeDashboard.Api/appsettings.json` contains the dashboard cards:

```json
{
  "Dashboard": {
    "Services": [
      {
        "Id": "plex",
        "Name": "Plex",
        "Description": "Media server",
        "Url": "http://server-pc:32400/web",
        "HealthUrl": "http://server-pc:32400/identity",
        "RestartEnabled": false
      }
    ]
  }
}
```

Use stable lowercase IDs because restart commands and agent Windows-service mappings target those IDs.

## Service integrations

Each service supports a `Kind` value. `Generic` uses only `HealthUrl`; first-class integrations use their native APIs and show metric chips in the dashboard.

Supported kinds:

- `Plex`
- `Sonarr`
- `Radarr`
- `Lidarr`
- `Readarr`
- `Prowlarr`
- `Bazarr`
- `qBittorrent`
- `SABnzbd`
- `Jellyfin`
- `GameServer`
- `FileShare`
- `Generic`

Example *arr app:

```json
{
  "Id": "radarr",
  "Name": "Radarr",
  "Kind": "Radarr",
  "Description": "Movie library automation",
  "Url": "http://server-pc:7878",
  "HealthUrl": "http://server-pc:7878/ping",
  "ApiKey": "radarr-api-key",
  "RestartEnabled": false
}
```

If an *arr API key is omitted, HomeDashboard falls back to the configured `HealthUrl`. With an API key, *arr checks include version, OS, health issue count, and queue count when those endpoints are available.

Example Plex:

```json
{
  "Id": "plex",
  "Name": "Plex",
  "Kind": "Plex",
  "Description": "Media server",
  "Url": "http://server-pc:32400/web",
  "ApiKey": "optional-plex-token",
  "RestartEnabled": false
}
```

Example download clients:

```json
{
  "Id": "qbittorrent",
  "Name": "qBittorrent",
  "Kind": "qBittorrent",
  "Description": "Torrent download client",
  "Url": "http://server-pc:8080",
  "RestartEnabled": false
}
```

```json
{
  "Id": "sabnzbd",
  "Name": "SABnzbd",
  "Kind": "SABnzbd",
  "Description": "Usenet download client",
  "Url": "http://server-pc:8085",
  "ApiKey": "optional-sabnzbd-api-key",
  "RestartEnabled": false
}
```

## Authentication and secrets

Browser users sign in through `/auth/login` with `Security:DashboardPassword` or `Security:DashboardPasswordHash`. The first-run setup UI writes `appsettings.Local.json` with a password hash and generated API keys when you leave key fields blank. Restart the API after saving setup so all new values are loaded.

After first-run setup, authenticated users can open the gear menu to manage the default agent, service cards, integration keys, restart-control flags, and custom RSS or podcast feeds. The editor never returns stored API keys to the browser. A blank key preserves the saved value; **Clear saved key** explicitly removes it. Changes are written atomically to `appsettings.Local.json`, preserve dashboard and agent credentials plus session settings, and take effect after the API service restarts.

The **Custom feeds** tab accepts standard OPML 1.0 and 2.0 subscription exports up to 2 MB. Feed outlines with HTTP or HTTPS `xmlUrl` values are imported; `title` or `text` supplies the feed name, `htmlUrl` supplies the provider page, and nested folder names become categories. Repeated URLs, invalid URLs, DTDs, and external entities are rejected or skipped. Imports remain an unsaved preview until **Save changes** is selected.

Use machine names or LAN addresses that are reachable from the PC running the API. `localhost` always means the API PC, not the computer viewing the dashboard in a browser.

Dashboard automation can still call dashboard endpoints with `X-HomeDashboard-Key: Security:DashboardApiKey`. Agent write/poll endpoints use `Security:AgentApiKey`.

API settings:

```json
{
  "Security": {
    "DashboardApiKey": "replace-with-a-dashboard-key",
    "AgentApiKey": "replace-with-a-different-agent-key",
    "DashboardPassword": "replace-with-a-browser-password",
    "DashboardPasswordHash": ""
  }
}
```

Agent settings:

```json
{
  "Agent": {
    "DashboardApiUrl": "http://dashboard-pc:5000",
    "ApiKey": "replace-with-the-agent-key"
  }
}
```

Use different dashboard and agent keys. The dashboard key can read normal dashboard endpoints; the agent key is accepted only for snapshot and command endpoints.

For deployed web builds served by the API executable, no frontend secret is needed. For Vite local development, set `VITE_API_BASE_URL` only when the API is on another origin.

## Persistence

The API stores latest agent snapshots, rolling history, queued command state, and restart audit history in `Dashboard:DataPath`.

```json
{
  "Dashboard": {
    "DataPath": "data/homedashboard-state.json",
    "AgentHistoryLimit": 120
  }
}
```

## RSS feeds

HomeDashboard includes a recommended catalog of technology, development, infrastructure, cybersecurity, and podcast feeds. Recommended podcast entries use official RSS for new episodes and include a Spotify discovery link. Disable the catalog while keeping only your own feeds with `Dashboard:IncludeRecommendedFeeds` set to `false`.

Add custom feeds under `Dashboard:NewsFeeds`:

```json
{
  "Name": "Ars Technica",
  "Url": "https://feeds.arstechnica.com/arstechnica/index",
  "Kind": "Article",
  "Category": "Technology"
}
```

Custom podcast feed:

```json
{
  "Name": "Example Security Show",
  "Url": "https://example.com/podcast.xml",
  "Kind": "Podcast",
  "Category": "Cybersecurity",
  "ProviderUrl": "https://open.spotify.com/search/Example%20Security%20Show"
}
```

Supported `Kind` values are `Article` and `Podcast`. The intelligence stream searches title, summary, source, and category, and can also filter by kind and topic. Feed results are cached for ten minutes; failed refreshes retain the previous successful items.

## Web API URL

For local development with Vite, requests proxy to `http://localhost:5000`. For deployed environments, set:

```text
VITE_API_BASE_URL=https://your-dashboard-api
```

## Agent services

`src/HomeDashboard.Agent/appsettings.json` defines Windows services the agent should observe and optionally restart:

```json
{
  "Agent": {
    "AgentId": "server-pc",
    "WindowsServices": [
      {
        "Id": "plex",
        "DisplayName": "Plex Media Server",
        "ServiceName": "Plex Media Server",
        "RestartEnabled": false
      }
    ]
  }
}
```

The API uses `Dashboard:DefaultAgentId` to decide which agent snapshot should drive dashboard system stats and where restart commands should be queued. A restart command requires browser confirmation and only runs when both the API service card and the agent service entry have `RestartEnabled: true`.

Saving first-run setup synchronizes the generated agent key and default agent ID into the packaged agent's `appsettings.Local.json`. The Windows installer repeats this synchronization during installs and updates while preserving other agent settings. The agent watches this local file for changes, so credential corrections do not require republishing the executable.

Fresh agent snapshots supply CPU, memory, disks, uptime, operating system, reboot state, network rates, and top-process telemetry. If the selected agent has not reported for two minutes, the dashboard marks it stale and uses telemetry collected by the API host instead of displaying an old snapshot indefinitely.

## Alerts, audit, and live updates

The dashboard includes active alerts for offline/degraded services, stale agents, and disks above 90% usage. Restart queue/rejection/completion records appear in the audit panel and are also available from `GET /api/audit`.

The browser subscribes to `GET /api/events` for server-sent dashboard updates and keeps polling as a fallback.

## Windows package

Create the executable package with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-windows.ps1
```

Run `outputs/HomeDashboard-Windows/api/HomeDashboard.Api.exe` for the dashboard/API and `outputs/HomeDashboard-Windows/agent/HomeDashboard.Agent.exe` on the services PC. The optional installer scripts in `outputs/HomeDashboard-Windows/tools` create Windows services for those executables. `install-homedashboard.ps1` wraps both service installers.
