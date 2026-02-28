using Rocket.API;
using Rocket.Unturned.Player;
using System.Collections.Generic;
using RocketLogger = Rocket.Core.Logging.Logger;

namespace LeaderosConnect
{
    public class CommandLeaderosStatus : IRocketCommand
    {
        public string Name => "leaderos.status";
        public string Help => "Shows LeaderosConnect WebSocket connection status";
        public string Syntax => "/leaderos.status";
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string> { "lostatus" };
        public List<string> Permissions => new List<string> { "leaderos.status" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (caller is UnturnedPlayer player && !player.IsAdmin)
            {
                RocketLogger.LogWarning("[LeaderosConnect] Unauthorized status command attempt.");
                return;
            }

            LeaderosConnect.Instance.ExecuteStatusCommand(caller);
        }
    }

    public class CommandLeaderosReconnect : IRocketCommand
    {
        public string Name => "leaderos.reconnect";
        public string Help => "Reconnects the WebSocket connection";
        public string Syntax => "/leaderos.reconnect";
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string> { "loreconnect" };
        public List<string> Permissions => new List<string> { "leaderos.reconnect" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (caller is UnturnedPlayer player && !player.IsAdmin)
            {
                RocketLogger.LogWarning("[LeaderosConnect] Unauthorized reconnect command attempt.");
                return;
            }

            LeaderosConnect.Instance.ExecuteReconnectCommand(caller);
        }
    }

    public class CommandLeaderosDebug : IRocketCommand
    {
        public string Name => "leaderos.debug";
        public string Help => "Toggles debug mode for LeaderosConnect";
        public string Syntax => "/leaderos.debug";
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public List<string> Aliases => new List<string> { "lodebug" };
        public List<string> Permissions => new List<string> { "leaderos.debug" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (caller is UnturnedPlayer player && !player.IsAdmin)
            {
                RocketLogger.LogWarning("[LeaderosConnect] Unauthorized debug command attempt.");
                return;
            }

            LeaderosConnect.Instance.ExecuteDebugCommand(caller);
        }
    }
}