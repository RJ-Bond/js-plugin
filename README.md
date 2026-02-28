# JSMonitor Plugin for V Rising

BepInEx server-side plugin that pushes live map data (online players + castle positions) to [JSMonitor](https://github.com/RJ-Bond/js-monitoring).

## Requirements

| Item | Version |
|------|---------|
| V Rising Dedicated Server | latest |
| BepInEx IL2CPP | 6.0.0-be.733+ |
| .NET SDK | 6.0 |

## Installation

1. Install [BepInEx IL2CPP](https://builds.bepinex.dev/projects/bepinex_be) on your V Rising dedicated server.
2. Build this project: `dotnet build -c Release`
3. Copy `JSMonitorPlugin/bin/Release/net6.0/JSMonitorPlugin.dll` to `BepInEx/plugins/` on your server.
4. Start the server once — BepInEx will generate a config file at `BepInEx/config/JSMonitorPlugin.cfg`.
5. Stop the server, edit the config:

```ini
[General]
## Full URL of your JSMonitor instance
JSMonitorUrl = https://your-jsmonitor.example.com

## API token from your JSMonitor profile (Profile → Generate API Token)
ApiKey = your_api_token_here

## Server ID in JSMonitor (visible in the admin panel or URL)
ServerId = 1

## Push interval in seconds (minimum 10)
UpdateInterval = 60
```

6. Restart the server.

## How it works

Every `UpdateInterval` seconds the plugin:
1. Queries the V Rising ECS world for **all connected players** and their positions.
2. Queries all **castle hearts** and their owner/tier/position.
3. POSTs a JSON payload to `POST /api/v1/vrising/push` on your JSMonitor instance.

JSMonitor stores the latest snapshot per server and serves it to the frontend map component.

## Frontend

In JSMonitor, open any **V Rising** server card → click **🗺️ Живая карта** tab to see the live map.

## Payload format

```json
{
  "server_id": 1,
  "players": [
    { "name": "VampireKing", "clan": "NightLords", "x": -1200.5, "z": 800.3, "health": 0.95 }
  ],
  "castles": [
    { "owner": "VampireKing", "clan": "NightLords", "x": -1180.0, "z": 820.0, "tier": 3 }
  ]
}
```

## Security

The plugin authenticates using your JSMonitor **API Token** (`X-API-Key` header).
Only the server owner or admin can push data for a server.

## License

MIT
