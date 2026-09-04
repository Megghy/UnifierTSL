using MySqlX.XDevAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Terraria;
using TShockAPI.Configuration;
using TShockAPI.DB;
using UnifierTSL;
using UnifierTSL.Network;
using UnifierTSL.Servers;

namespace TShockAPI
{
    public partial class TSPlayer
    {
        public Player TPlayer => FakePlayer ?? UnifiedServerCoordinator.GetPlayer(Index);
        public bool RealPlayer {
            get { return Index >= 0 && Index < Main.maxPlayers && TPlayer != null; }
        }
        public virtual ServerContext GetCurrentServer() {
            if (Index >= 0 && Index < UnifiedServerCoordinator.globalClients.Length) {
                var server = UnifiedServerCoordinator.GetClientCurrentlyServer(Index);
                if (server is not null)
                    return server;
            }
            return UnifiedServerCoordinator.GetDefaultServer()!;
        }
        public RemoteClient Client => UnifiedServerCoordinator.globalClients[Index];
        public TShockSettings GetCurrentSettings() {
            var config = TShock.Config;
            if (config is null)
                return new TShockSettings();

            var server = GetCurrentServer();
            return server is not null
                ? config.GetServerSettings(server.Name)
                : config.GetServerSettings(string.Empty);
        }
        public LocalClientSender MsgSender => UnifiedServerCoordinator.clientSenders[Index];
    }
}
