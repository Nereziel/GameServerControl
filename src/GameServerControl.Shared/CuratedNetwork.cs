namespace GameServerControl.Shared;

/// <summary>
/// Keeps curated profile port reservations aligned with the launch arguments that the game
/// actually consumes. This also repairs older definitions where NetworkPorts could be edited
/// independently from the game-specific configuration.
/// </summary>
public static class CuratedNetwork
{
    public static ServerDef Normalize(ServerDef def)
    {
        return def.SteamAppId switch
        {
            "896660" => NormalizeValheim(def),
            "4019830" => NormalizeDragonwilds(def),
            _ => def
        };
    }

    private static ServerDef NormalizeValheim(ServerDef def)
    {
        var port = ReadSeparatedInt(def.StartArgs, "-port")
            ?? FindNamedPort(def, "Game")
            ?? 2456;
        if (port is < 1 or > 65534) return def;

        return def with
        {
            NetworkPorts = new[]
            {
                new NetworkPort("Game", NetworkProtocol.Udp, port, port + 1)
            }
        };
    }

    private static ServerDef NormalizeDragonwilds(ServerDef def)
    {
        var game = ReadInlineInt(def.StartArgs, "-Port=")
            ?? ReadSeparatedInt(def.StartArgs, "-Port")
            ?? FindNamedPort(def, "Game")
            ?? 7777;
        var beacon = ReadInlineInt(def.StartArgs, "-BeaconPort=")
            ?? ReadSeparatedInt(def.StartArgs, "-BeaconPort")
            ?? FindNamedPort(def, "Beacon")
            ?? 8888;
        if (game is < 1 or > 65535 || beacon is < 1 or > 65535) return def;

        return def with
        {
            NetworkPorts = new[]
            {
                new NetworkPort("Game", NetworkProtocol.Udp, game),
                new NetworkPort("Beacon", NetworkProtocol.Udp, beacon)
            }
        };
    }

    private static int? FindNamedPort(ServerDef def, string name) =>
        def.NetworkPorts?
            .FirstOrDefault(p => p.Protocol == NetworkProtocol.Udp &&
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?
            .Port;

    private static int? ReadInlineInt(string[]? args, string prefix)
    {
        foreach (var arg in args ?? Array.Empty<string>())
        {
            if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(arg[prefix.Length..], out var value)) return value;
        }
        return null;
    }

    private static int? ReadSeparatedInt(string[]? args, string key)
    {
        var values = args ?? Array.Empty<string>();
        for (var i = 0; i + 1 < values.Length; i++)
        {
            if (!values[i].Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(values[i + 1], out var value)) return value;
        }
        return null;
    }
}
