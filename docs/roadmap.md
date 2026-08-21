# Roadmap

## MVP

- Configurable service cards.
- Per-card health checks and first-class checks for Plex, *arr apps, qBittorrent, SABnzbd, and Jellyfin.
- Dashboard snapshot endpoint.
- Disk, memory, and Windows host CPU stats with fallback sampling.
- RSS/Atom news feed aggregation.
- Persisted restart command queue with confirmation, audit history, and agent-side Windows service execution.
- Windows-focused agent with configured service status lookup.
- First-run setup flow, hashed dashboard password support, dashboard API key support, and separate agent API-key authentication.
- Agent snapshot publishing, persisted heartbeat history, and latest-snapshot dashboard usage.
- Alerts for unhealthy services, stale agents, and nearly-full disks.
- Server-sent event live updates.
- React dashboard shell.
- Windows publish zip with API/agent executables and service installer helpers.
- CI for .NET and web.

## Next

- Full config editing UI after first-run.
- User accounts or external identity provider support.
- Email/Discord/Push notification delivery.
- Signed MSI installer with automatic service upgrade.
- More detailed mobile views after real deployment feedback.
