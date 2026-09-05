# Personal command center

The **Command** workspace combines daily planning, household context, home controls, and homelab operations without replacing the existing service, operations, or intelligence screens.

## Daily workflow

- **Today** shows a generated briefing, agenda, open tasks, priority inbox, deliveries, shopping, media requests, quick notes, and recent activity.
- **Plan** manages calendar entries, categorized tasks, priority and due dates, notes, shopping lists, packages, media requests, and household profiles.
- **Home** groups Home Assistant entities by room and provides scene and entity controls.
- **Systems** combines custom operational assets with Windows files, system logs, Wake-on-LAN, and guarded machine actions.
- **Automate** supports manual rules and `daily at HH:mm`, `every N minutes`, `task overdue`, and `mode NAME` triggers.
- **Connect** configures external systems. Blank secret fields preserve the current secret.

The **Plan** workspace includes 20 daily-use productivity features: task search, list filtering, priority filtering, smart sorting, due-date grouping, bulk completion, completed-task cleanup, one-day postponement, a seven-day agenda, calendar filtering, shopping-list filtering, purchase progress, bulk purchase, purchased-item cleanup, copyable shopping lists, note search, note tag filtering, note pinning, inbox severity filtering, and bulk acknowledgement. Filters persist in the browser. Bulk operations are sent as one bounded request and are limited to safe local task, shopping, notification, and deletion operations.

Use the command palette or `Ctrl+K` to search tasks, calendar entries, notes, packages, media, assets, and home entities. Browser speech recognition can fill the assistant prompt when the browser supports it. The built-in assistant answers local dashboard questions deterministically; Ollama can supply conversational responses when configured. Suggested actions are proposals and still require approval.

## Modes and layouts

Home, Away, Sleep, Work, Gaming, Movie, and Guest modes create shared context for automations. Widget visibility is customizable per browser. Wall display uses fullscreen and a denser overview; phone layouts preserve all commands without page overflow. Production builds register the PWA service worker, while API requests always remain network-first.

## Accounts and authorization

The original dashboard password signs in as the Owner Administrator. Additional household accounts can be created with Administrator, Member, or Viewer roles:

- **Administrator** can configure connectors, restore backups, and approve machine controls.
- **Member** can manage personal command-center items and normal actions.
- **Viewer** has read-only access.

Passwords are stored as hashes and never returned by the API. Connector secrets are masked and are excluded from backup exports. API keys retain their existing automation behavior.

## Connectors

The catalog includes Home Assistant, ntfy, Ollama, Discord, Microsoft 365, Google, CalDAV, GitHub, UniFi, Pi-hole/AdGuard Home, Overseerr/Jellyseerr, backup systems, UPS/power, cameras, MQTT, package tracking, utilities, game servers, email summary, generic webhooks, and Windows workspace.

Home Assistant, ntfy, Ollama, Windows, Wake-on-LAN, and generic HTTP/webhook actions have direct handling. Other systems use the normalized connector adapter so one small bridge can expose data from an existing API without adding vendor credentials to the browser. An enabled connector endpoint may return:

```json
{
  "status": "Connected",
  "assets": [],
  "calendar": [],
  "packages": [],
  "mediaRequests": [],
  "notifications": []
}
```

The configured secret is sent as a Bearer token. Inbound events can be posted to `/api/command-center/webhooks/{source}` with `X-HomeDashboard-Key`; events can create notifications or update assets. MQTT actions use an HTTP bridge endpoint, keeping broker libraries and credentials outside the dashboard process.

## Discord remote capture

Discord connects outbound from the API through the Discord gateway, so no new inbound port is required. Create an application and bot in the Discord Developer Portal, enable the **Message Content Intent**, invite the bot to your private server with permission to view and send messages, then open **Command > Connect > Discord**. Save the bot token as the credential and add at least one allowed Discord user ID. Optional channel and server ID lists further restrict access. Discord Developer Mode exposes the **Copy ID** command for users, channels, and servers.

The bot ignores every user not present in the user allowlist. It will not connect when that list is empty. IDs may be separated by commas, spaces, or semicolons. After saving, the connector status changes to the bot username when the gateway is ready.

HomeDashboard registers a guided `/home` slash command after connecting. Discord shows its command groups, descriptions, choices, and matching dashboard items while you type. Configure at least one allowed server ID for server-scoped commands that appear immediately. With no server IDs, the bot registers globally and Discord may take time to distribute a new command. Slash replies are private to the caller and still enforce the configured user, channel, and server allowlists.

`/home` supports task, shopping, calendar, note, package, media, inbox, notify, reminder, automation, assistant, device, and system groups. You can add, list, complete, reopen, defer, prioritize, move, acknowledge, snooze, search, pin, bulk-buy, bulk-clear, bulk-acknowledge, update package status, inspect integrations, assets, profiles, logs, activity, and Home Assistant devices, run or create automations, create reminders, send ntfy notifications, ask the local assistant, and change household mode. Existing-item fields autocomplete against HomeDashboard data. Device control requires an explicit confirmation option. Windows lock, sleep, restart, and shutdown commands are intentionally not exposed through Discord.

Default commands use the `!hd` prefix:

```text
!hd shopping add milk, bread | Groceries
!hd shopping done milk
!hd shopping buy_all Groceries
!hd shopping clear_purchased Groceries
!hd shopping list
!hd task add Renew certificate | 2026-09-01 18:00 | High | Home
!hd task done renew certificate
!hd task defer renew certificate | 2
!hd task priority renew certificate | Urgent
!hd task move renew certificate | Servers
!hd task list
!hd task today
!hd task overdue
!hd agenda add Dentist | 2026-09-03 14:00 | Downtown
!hd agenda today
!hd agenda week
!hd note add Project idea | Details
!hd note search project
!hd note pin project | true
!hd package add Keyboard | UPS | 1Z... | 2026-09-04
!hd package update keyboard | Delivered
!hd media search Dune Part Two
!hd inbox list
!hd inbox ack_all
!hd notify send Server alert | Plex needs attention
!hd reminder add Check UPS | Replace battery this weekend
!hd automation add Morning check | daily at 08:00 | notification.create | Check dashboard
!hd automations list
!hd assistant ask What needs attention?
!hd assistant brief
!hd devices list
!hd mode set Away
!hd search certificate
!hd integrations list
!hd assets list
!hd system logs
!hd status
!hd help
```

Dates are interpreted in the API computer's local culture and time zone. `today` and `tomorrow` are accepted for quick task, agenda, and package capture. The command prefix can be changed in the Discord connector.

The Discord intake command also supports an operational readout batch for quick phone checks:

```text
services list
services offline
services degraded
services show <id or name>
agents
alerts
commands
system
network
storage
uptime
processes
incidents
downloads
downloads pause <id or name>
downloads resume <id or name>
downloads recheck <id or name>
downloads remove <id or name>
arr health
arr status
arr queue
arr missing
arr history
arr calendar
arr search-missing <service> confirm
arr refresh <service>
playback
maintenance list
maintenance remove <id or title>
activity
```

These commands are read-only except `maintenance remove`, which removes an existing maintenance window by id, exact title, or partial title.

For verified media requests, configure the Sonarr and Radarr service URLs and API keys in HomeDashboard, plus at least one root folder and quality profile in each application. `/home media search` queries both catalogs. `/home media add` provides native Discord autocomplete in its `media_title` field with year, media type, source application, and IMDb ID, then adds the selected release directly to Sonarr or Radarr. Discord does not provide native autocomplete while typing an ordinary prefix message, so an ambiguous `!hd media add ...` response includes a release selection menu instead.

For immediate slash-command registration, set **Allowed server IDs** to the Discord server ID where the bot is installed. With no server ID, HomeDashboard registers `/home` globally. The Discord connector status reports whether server commands are ready, globally registered, or failed because a configured server ID does not contain the bot.

## Windows controls

The Windows connector accepts semicolon-separated allowed file roots. Browsing is constrained to those roots. System logs use the local Windows System event log. Wake-on-LAN accepts a target MAC address. Lock, sleep, restart, and shutdown are queued to `Dashboard:DefaultAgentId` and run only when `Agent:MachineActionsEnabled` is explicitly enabled.

## Storage, export, and updates

Command-center state is atomically written to `homedashboard-command-center.json` beside `Dashboard:DataPath`. The built-in dashboard export includes personal records and non-secret integration configuration. A restore keeps locally stored connector secrets and household account hashes. The Windows publisher and GitHub updater preserve the complete `data` directory and retain timestamped rollback backups.
