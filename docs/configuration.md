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

Browser users sign in through `/auth/login` with `Security:DashboardPassword`. The API sets an HttpOnly session cookie and the React app uses that cookie for dashboard requests.

Dashboard automation can still call dashboard endpoints with `X-HomeDashboard-Key: Security:DashboardApiKey`. Agent write/poll endpoints use `Security:AgentApiKey`.

API settings:

```json
{
  "Security": {
    "DashboardApiKey": "replace-with-a-dashboard-key",
    "AgentApiKey": "replace-with-a-different-agent-key",
    "DashboardPassword": "replace-with-a-browser-password"
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

The API stores latest agent snapshots, rolling history, and queued command state in `Dashboard:DataPath`.

```json
{
  "Dashboard": {
    "DataPath": "data/homedashboard-state.json",
    "AgentHistoryLimit": 120
  }
}
```

## RSS feeds

Add feeds under `Dashboard:NewsFeeds`:

```json
{
  "Name": "Ars Technica",
  "Url": "https://feeds.arstechnica.com/arstechnica/index"
}
```

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

The API uses `Dashboard:DefaultAgentId` to decide which agent snapshot should drive dashboard system stats and where restart commands should be queued. A restart command only runs when both the API service card and the agent service entry have `RestartEnabled: true`.

## Windows package

Create the executable package with:

```powershell
powershell -ExecutionPolicy Bypass -File tools/publish-windows.ps1
```

Run `outputs/HomeDashboard-Windows/api/HomeDashboard.Api.exe` for the dashboard/API and `outputs/HomeDashboard-Windows/agent/HomeDashboard.Agent.exe` on the services PC. The optional installer scripts in `outputs/HomeDashboard-Windows/tools` create Windows services for those executables.
