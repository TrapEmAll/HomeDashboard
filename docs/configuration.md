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
