# LeaderosConnect Plugin for Unturned

This plugin allows you to connect your Unturned server to LeaderOS, enabling you to send commands to the server through the LeaderOS platform.

---

### Requirements

- Unturned server with [RocketMod](https://github.com/RocketMod/Rocket) installed
- A LeaderOS account with an active server token

### Installation

1. [Download `LeaderosConnect.dll`](https://www.leaderos.net/plugin/unturned)
2. Upload it to your server:
   ```
   /Rocket/Plugins/LeaderosConnect.dll
   ```
3. Start the server once to generate the config file, then stop it
4. Edit the config file at:
   ```
   /Rocket/Plugins/LeaderosConnect/LeaderosConnect.configuration.xml
   ```
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <LeaderosConnectConfiguration xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
     <WebsiteUrl>https://yourdomain.com</WebsiteUrl>
     <ApiKey>YOUR_API_KEY</ApiKey>
     <ServerToken>YOUR_SERVER_TOKEN</ServerToken>
     <DebugMode>false</DebugMode>
     <CheckPlayerOnline>true</CheckPlayerOnline>
   </LeaderosConnectConfiguration>
   ```
5. Start the server

### Configuration

| Field | Description |
|-------|-------------|
| `WebsiteUrl` | The URL of your LeaderOS website (e.g., `https://yourwebsite.com`). |
| `ApiKey` | Your LeaderOS API key. Find it on `Dashboard > Settings > API` |
| `ServerToken` | Your server token. Find it on `Dashboard > Store > Servers > Your Server > Server Token` |
| `DebugMode` | Set to `true` to enable debug logging, or `false` to disable it. |
| `CheckPlayerOnline` | Set to `true` to check if players are online before sending commands, or `false` to skip this check. |

### Commands

| Command | Description |
|---------|-------------|
| `/leaderos.status` | Displays the status of the LeaderOS connection. |
| `/leaderos.reconnect` | Reconnects to the LeaderOS socket server |
| `/leaderos.debug` | Toggles debug mode on/off |

All commands require admin privileges.

---

## Development

### Requirements

- [.NET SDK](https://dotnet.microsoft.com/download) (any modern version)
- Rocket and Unturned DLL files (obtained from your server)

### Build

```bash
git clone https://github.com/leaderos-net/unturned-leaderos-connect
cd unturned-leaderos-connect
dotnet build
```

The output DLL will be at:
```
bin/Debug/netstandard2.0/LeaderosConnect.dll
```

### Project Structure

```
LeaderosConnect.cs               # Main plugin logic
LeaderosConnectConfiguration.cs  # Config class
LeaderosCommands.cs              # Rocket commands
libs/                            # Reference DLLs
```

---

## How It Works

1. On server start, the plugin connects to the LeaderOS WebSocket server
2. When a purchase is made on your website, LeaderOS sends a `send-commands` event
3. The plugin validates the commands against your website's API
4. If the player is online, commands are executed immediately
5. If the player is offline and `CheckPlayerOnline` is enabled, commands are saved to a local queue file and executed the next time the player joins

---

## License

MIT