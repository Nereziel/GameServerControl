namespace GameServerControl.Shared;

/// <summary>
/// Safe configuration roots associated with a curated game profile. Paths are always relative
/// to the registered server working directory; the Agent still applies its extension, size,
/// traversal and symlink protections before exposing any file.
/// </summary>
public sealed record GameConfigRoot(
    string RelativePath,
    string Group,
    bool Recursive = true);

/// <summary>
/// Operational metadata that does not belong in the launch/install contract itself. Keeping
/// this in Shared lets Agent services consume profile-specific behavior without duplicating
/// Steam App ID or profile-key switches.
/// </summary>
public static class GameProfileMetadata
{
    private static readonly IReadOnlyDictionary<string, GameConfigRoot[]> ConfigRoots =
        new Dictionary<string, GameConfigRoot[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["valheim"] =
            [
                new("BepInEx/config", "Mods / plugins"),
                new("BepInEx/plugins", "Mods / plugins")
            ],
            ["minecraft"] =
            [
                new("config", "Mods / plugins"),
                new("plugins", "Mods / plugins"),
                new("world/serverconfig", "World config")
            ],
            ["palworld"] =
            [
                new("Pal/Saved/Config", "Server"),
                new("Pal/Binaries/Win64/Mods", "Mods / plugins")
            ],
            ["satisfactory"] =
            [
                new("FactoryGame/Saved/Config", "Server"),
                new("FactoryGame/Mods", "Mods / plugins")
            ],
            ["dragonwilds"] =
            [
                new("RSDragonwilds/Saved/Config", "Server")
            ],
            ["vrising"] =
            [
                new("BepInEx/config", "Mods / plugins"),
                new("BepInEx/plugins", "Mods / plugins"),
                new("VRisingServer_Data/StreamingAssets/Settings", "Server")
            ],
            ["ark-asa"] =
            [
                new("ShooterGame/Saved/Config", "Server")
            ],
            ["ark-se"] =
            [
                new("ShooterGame/Saved/Config", "Server")
            ],
            ["7dtd"] =
            [
                new("data", "Server", false)
            ],
            ["dst"] =
            [
                new("data/DST", "Server")
            ],
            ["enshrouded"] =
            [
                new(".", "Server", false)
            ]
        };

    public static GameConfigRoot[] ConfigRootsFor(GameProfile? profile)
    {
        if (profile is null) return Array.Empty<GameConfigRoot>();
        return ConfigRoots.TryGetValue(profile.Key, out var roots)
            ? roots.ToArray()
            : Array.Empty<GameConfigRoot>();
    }
}
