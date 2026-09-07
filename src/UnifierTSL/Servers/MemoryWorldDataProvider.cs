using Terraria.IO;

namespace UnifierTSL.Servers;

/// <summary>
/// Creates a complete Terraria world in memory without reading or writing a .wld file.
/// </summary>
public sealed class MemoryWorldDataProvider : IWorldDataProvider
{
    private readonly Action<ServerContext>? initialize;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MemoryWorldDataProvider(
        string worldName,
        int width,
        int height,
        Action<ServerContext>? initialize = null,
        int gameMode = 2) {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        WorldName = worldName;
        Width = width;
        Height = height;
        GameMode = gameMode;
        this.initialize = initialize;
    }

    public string WorldName { get; }
    public string WorldFileName => $"<memory:{WorldName}>";
    public int Width { get; }
    public int Height { get; }
    public int GameMode { get; }
    public Task Ready => ready.Task;

    public WorldFileData ApplyMetadata(ServerContext server) {
        server.Main.worldName = WorldName;
        server.Main.maxTilesX = Width;
        server.Main.maxTilesY = Height;
        server.Main.GameMode = GameMode;
        server.Main.autoGen = false;
        server.Netplay.SaveOnServerExit = false;

        WorldFileData metadata = server.WorldFile.CreateMetadata(WorldName, false, GameMode);
        metadata.WorldSizeX = Width;
        metadata.WorldSizeY = Height;
        metadata.WorldId = Random.Shared.Next(1, int.MaxValue);
        server.Main.ActiveWorldFileData = metadata;
        return metadata;
    }

    internal void Initialize(ServerContext server) {
        try {
            server.WorldGen.loadFailed = false;
            server.WorldGen.clearWorld();

            server.Main.worldName = WorldName;
            server.Main.spawnTileX = Width / 2;
            server.Main.spawnTileY = Height / 2;
            server.Main.worldSurface = Height * 0.3;
            server.Main.rockLayer = Height * 0.45;

            initialize?.Invoke(server);
            server.WorldGen.Hooks.WorldLoaded();
            ready.TrySetResult();
        }
        catch (Exception exception) {
            server.WorldGen.loadFailed = true;
            ready.TrySetException(exception);
            throw;
        }
    }
}
