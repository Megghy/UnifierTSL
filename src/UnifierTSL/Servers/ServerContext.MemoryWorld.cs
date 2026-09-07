using UnifierTSL.Extensions;

namespace UnifierTSL.Servers;

public partial class ServerContext
{
    private static void InitializeMemoryWorlds() {
        On.Terraria.WorldGenSystemContext.serverLoadWorld += LoadMemoryWorld;
        On.Terraria.IO.WorldFileSystemContext.SaveWorld += SuppressMemoryWorldSave;
    }

    private static void SuppressMemoryWorldSave(
        On.Terraria.IO.WorldFileSystemContext.orig_SaveWorld orig,
        Terraria.IO.WorldFileSystemContext self,
        bool resetTime,
        bool useTemps,
        bool canBeSkipped) {
        if (self.root.ToServer().worldDataProvider is not MemoryWorldDataProvider) {
            orig(self, resetTime, useTemps, canBeSkipped);
        }
    }

    private static Task LoadMemoryWorld(
        On.Terraria.WorldGenSystemContext.orig_serverLoadWorld orig,
        Terraria.WorldGenSystemContext self) {
        ServerContext server = self.root.ToServer();
        if (server.worldDataProvider is not MemoryWorldDataProvider provider) {
            return orig(self);
        }

        return Task.Factory.StartNew(
            () => provider.Initialize(server),
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }
}
