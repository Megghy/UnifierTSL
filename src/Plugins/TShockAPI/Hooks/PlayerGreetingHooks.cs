using System;
using System.ComponentModel;

namespace TShockAPI.Hooks;

public sealed class PlayerGreetingEventArgs(TSPlayer player) : HandledEventArgs
{
    public TSPlayer Player { get; } = player;
}

/// <summary>Allows a player-specific MOTD without changing the shared MOTD file.</summary>
public static class PlayerGreetingHooks
{
    public static event Action<PlayerGreetingEventArgs>? Greeting;
    internal static bool Invoke(TSPlayer player)
    {
        var args = new PlayerGreetingEventArgs(player);
        Greeting?.Invoke(args);
        return args.Handled;
    }
}
