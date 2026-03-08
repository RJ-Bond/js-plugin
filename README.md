# JSMonitor Plugin for V Rising

BepInEx server-side plugin for V Rising that integrates with [JSMonitor](https://github.com/RJ-Bond/js-monitoring). Pushes live map data, moderation records and chat events to the web panel; receives moderation commands and auto-announcements in return.

---

## Features

- **Live map push** — player positions, castle locations, free plots pushed every N seconds
- **Moderation system** — kick, ban (temporary or permanent), mute, warn with full history
- **Auto-announcements** — configurable rotating messages with per-announcement intervals and optional random order
- **Remote moderation** — kick/ban commands issued from the web panel are executed on the next push cycle
- **Chat event log** — chat messages and connect/disconnect events are forwarded to the panel
- **Auto-admin mode** — player becomes admin automatically when they join alone (last admin flag)
- **Chat suppression** — muted players' messages are silently discarded server-side
- **In-game commands** — full `.kick`, `.ban`, `.mute`, `.warn`, `.announce` command set with VCF

---

## Requirements

| Item | Version |
|------|---------|
| V Rising Dedicated Server | latest |
| BepInEx IL2CPP | 6.0.0-be.733+ |
| .NET SDK | 6.0 |
| JSMonitor panel | v2.5.0+ |

---

## Installation

1. Install [BepInEx IL2CPP](https://builds.bepinex.dev/projects/bepinex_be) on your V Rising dedicated server.
2. Download `JSMonitorPlugin.dll` from the [latest release](https://github.com/RJ-Bond/js-plugin/releases/latest).
3. Copy the DLL to `BepInEx/plugins/` on your server.
4. Start the server once — BepInEx generates a config at `BepInEx/config/JSMonitorPlugin.cfg`.
5. Stop the server, edit the config:

```ini
[General]
## Full URL of your JSMonitor instance (no trailing slash)
JSMonitorUrl = https://your-jsmonitor.example.com

## API token from your JSMonitor profile (Profile → Generate API Token)
ApiKey = your_api_token_here

## Server ID in JSMonitor (visible in the admin panel or the server URL)
ServerId = 1

## How often to push data in seconds (minimum 10)
UpdateInterval = 60
```

6. Restart the server.

> **Build from source:** `dotnet build -c Release`
> Output: `JSMonitorPlugin/bin/Release/net6.0/JSMonitorPlugin.dll`

---

## How it works

Every `UpdateInterval` seconds the plugin:

1. Collects **players**, **castles**, **free plots**, **ban/mute/warn lists** from the V Rising ECS world.
2. `POST /api/v1/vrising/push` — sends JSON snapshot to JSMonitor.
3. Reads the response body which may contain:
   - **Moderation commands** — kick / ban / unban / announce queued from the web panel.
   - **Auto-announcement list** — messages with per-message intervals and random-order flag.

The plugin executes incoming commands immediately and hands the announcement list to `AutoAnnouncer`, which runs its own 5-second check loop.

```
Plugin → POST /api/v1/vrising/push ──► JSMonitor
         ◄── { ok, commands[], announcements[], announcements_random }
```

---

## In-game commands

All commands use the **VampireCommandFramework** prefix (`.`). Admin role is required unless noted.

### Moderation

| Command | Description |
|---------|-------------|
| `.kick <name> [reason]` | Kick a player from the server |
| `.ban <name> [duration] [reason]` | Ban a player. Duration: `1h`, `2d`, `1w`, or omit for permanent |
| `.unban <steamid>` | Lift a ban by Steam ID |
| `.mute <name> [duration] [reason]` | Mute a player. Duration same format as ban |
| `.unmute <name>` | Unmute a player |
| `.warn <name> <reason>` | Issue a warning (stored in history) |

### Announcements

| Command | Description |
|---------|-------------|
| `.announce <message>` | Broadcast a formatted announcement to all players |
| `.a <message>` | Shorthand for `.announce` |

### Utility

| Command | Description |
|---------|-------------|
| `.admins` | List online admins |
| `.autoadmin` | Toggle auto-admin mode (become admin when you join alone) |
| `.js-help` | Show all available plugin commands |

---

## Auto-announcements

The list of announcements is managed entirely from the JSMonitor web panel (`Admin → V Rising → Announcements`). The plugin receives the current list on every push and runs them autonomously:

- Each announcement has its own **interval in seconds**. When the due time arrives the message is sent.
- **Normal mode** — all due announcements are sent in `sort_order` order.
- **Random mode** — one announcement is picked at random from those that are currently due.

Announcement format in chat:
```
[!] Your message here
━━━━━━━━━━━━━━━━━━━━━━━━
```

The `[!]` prefix is coloured green; the separator is red. Messages support Unity TextMeshPro rich-text tags (`<color=#hex>`, `<b>`, `<i>`, `<s>`, `<size=N>`).

---

## Moderation broadcast messages

Kick and ban actions broadcast a coloured message to all players in Russian:

- **Kick:** `* Игрок [Name] был кикнут админом [Admin]. Причина: [reason]`
- **Ban:** `* Игрок [Name] забанен … на [duration]. Причина: [reason]`

---

## Chat event forwarding

The plugin intercepts in-game chat via a Harmony patch and queues events (type `chat`, `connect`, `disconnect`). They are sent in bulk to `POST /api/v1/vrising/events` and appear in the **Events** tab of the web panel.

---

## Payload format

```json
{
  "server_id": 1,
  "players": [
    { "name": "VampireKing", "clan": "NightLords", "x": -1200.5, "z": 800.3, "health": 0.95, "is_admin": false }
  ],
  "castles": [
    { "owner": "VampireKing", "clan": "NightLords", "x": -1180.0, "z": 820.0, "tier": 3, "name": "Dark Fortress" }
  ],
  "free_plots": [{ "x": -900.0, "z": 700.0 }],
  "bans":  [...],
  "mutes": [...],
  "warns": [...]
}
```

Push response:
```json
{
  "ok": true,
  "commands": [
    { "id": 1, "type": "kick", "player_name": "Griefer", "reason": "rule violation" }
  ],
  "announcements": [
    { "id": 1, "message": "Server rules: <b>no griefing</b>", "interval_seconds": 1800 }
  ],
  "announcements_random": false
}
```

---

## Security

- Authentication uses your JSMonitor **API Token** (`X-API-Key` header).
- Only the server owner or a panel admin can push data for a given server.
- The plugin bypasses TLS certificate validation to support self-signed and Let's Encrypt certificates.

---

## License

MIT
