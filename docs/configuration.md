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

Use stable lowercase IDs because restart commands and future agent mappings will target those IDs.

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

If an *arr API key is omitted, HomeDashboard falls back to the configured `HealthUrl`.

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

## Authentication

All dashboard API routes under `/api` require an API key in the `X-HomeDashboard-Key` header.

API settings:

```json
{
  "Security": {
    "DashboardApiKey": "replace-with-a-dashboard-key",
    "AgentApiKey": "replace-with-a-different-agent-key"
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

Web settings:

```text
VITE_API_BASE_URL=http://dashboard-pc:5000
VITE_DASHBOARD_API_KEY=replace-with-the-dashboard-key
```

Use different dashboard and agent keys. The dashboard key can read normal dashboard endpoints; the agent key is accepted only for `/api/agent/*`.

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

`src/HomeDashboard.Agent/appsettings.json` defines Windows services the agent should eventually observe:

```json
{
  "Agent": {
    "AgentId": "server-pc",
    "WindowsServices": [
      {
        "Id": "plex-service",
        "DisplayName": "Plex Media Server",
        "ServiceName": "Plex Media Server",
        "RestartEnabled": false
      }
    ]
  }
}
```

The API uses `Dashboard:DefaultAgentId` to decide which agent snapshot should drive the dashboard's system stats.
