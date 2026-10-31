namespace GameServerControl.Shared;

public enum GameConsoleMode
{
    None,
    LogOnly,
    Rcon
}

public sealed record GameCapabilities(
    bool ManagedConfig = false,
    bool Mods = false,
    bool Rcon = false,
    bool PlayerManagement = false,
    bool LiveLog = false,
    bool HyperV = true,
    bool ScheduledTask = true,
    GameConsoleMode ConsoleMode = GameConsoleMode.None);

public sealed record GameProfile(
    string Key,
    string Label,
    string Icon,
    GameType GameType,
    string? SteamAppId,
    string InstallDirectoryName,
    string ExecutableRelative,
    string[] DefaultStartArgs,
    string[] SaveDirs,
    string? LogPath,
    GameCapabilities Capabilities,
    int? DefaultRconPort = null,
    string? Notes = null)
{
    public string DefaultWindowsInstallPath => string.IsNullOrWhiteSpace(InstallDirectoryName)
        ? ""
        : $@"C:\GameServers\{InstallDirectoryName}";

    public string BuildExecutablePath(string root)
        => string.IsNullOrWhiteSpace(root) ? ExecutableRelative : Path.Combine(root, ExecutableRelative);

    public string[] BuildStartArgs(string root)
        => DefaultStartArgs.Select(x => Expand(x, root)).ToArray();

    public string[] BuildSaveDirs(string root)
        => SaveDirs.Select(x => Expand(x, root)).ToArray();

    public string? BuildLogPath(string root)
        => string.IsNullOrWhiteSpace(LogPath) ? null : Expand(LogPath, root);

    private static string Expand(string value, string root)
        => value.Replace("{root}", root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Single source of truth for user-facing game capabilities and safe defaults.
/// Both the Agent API and WPF client consume these profiles so unsupported settings
/// (for example RCON on Valheim) are not presented to users.
/// </summary>
public static class GameProfiles
{
    private static readonly GameCapabilities Basic = new();

    public static readonly GameProfile[] All =
    {
        new("custom", "Custom / blank", "🖥", GameType.Custom, null, "", "", Array.Empty<string>(), Array.Empty<string>(), null, Basic,
            Notes: "Advanced/manual server definition."),

        new("windrose", "Windrose", "🌹", GameType.Windrose, "4129620", "WindroseServer", "WindroseServer.exe",
            Array.Empty<string>(), new[]{ @"{root}\Saves" }, null,
            new GameCapabilities(ManagedConfig: true)),

        new("valheim", "Valheim", "⚔", GameType.SteamGeneric, "896660", "Valheim", "valheim_server.exe",
            new[]{ "-nographics", "-batchmode", "-name", "MyServer", "-port", "2456", "-world", "MyWorld", "-password", "change-me",
                   "-savedir", @"{root}\data", "-logFile", @"{root}\logs\valheim.log" },
            new[]{ @"{root}\data" }, @"{root}\logs\valheim.log",
            new GameCapabilities(ManagedConfig: true, Mods: true, LiveLog: true, ConsoleMode: GameConsoleMode.LogOnly),
            Notes: "World data and permission files are kept in the server folder via -savedir."),

        new("dragonwilds", "RuneScape: Dragonwilds", "🐉", GameType.SteamGeneric, "4019830", "Dragonwilds", "RSDragonwilds.exe",
            new[]{ "-log", "-NewConsole" },
            new[]{ @"{root}\RSDragonwilds\Saved\Savegames" }, @"{root}\RSDragonwilds\Saved\Logs\RSDragonwilds.log",
            new GameCapabilities(ManagedConfig: true, LiveLog: true, ConsoleMode: GameConsoleMode.LogOnly)),

        new("ark-asa", "ARK: Survival Ascended", "🦖", GameType.SteamGeneric, "2430930", "ArkAscended", @"ShooterGame\Binaries\Win64\ArkAscendedServer.exe",
            new[]{ "TheIsland_WP?listen?MaxPlayers=20?ServerPassword=?ServerAdminPassword=change-me" },
            new[]{ @"{root}\ShooterGame\Saved" }, null,
            new GameCapabilities(Mods: true)),

        new("ark-se", "ARK: Survival Evolved", "🦖", GameType.SteamGeneric, "376030", "ArkSE", @"ShooterGame\Binaries\Win64\ShooterGameServer.exe",
            new[]{ "TheIsland?listen?MaxPlayers=20?ServerAdminPassword=change-me", "-server", "-log" },
            new[]{ @"{root}\ShooterGame\Saved" }, null,
            new GameCapabilities(Mods: true)),

        new("palworld", "Palworld", "🐾", GameType.SteamGeneric, "2394010", "PalServer", @"PalServer\PalServer.exe",
            new[]{ "-useperfthreads", "-NoAsyncLoadingThread", "-UseMultithreadForDS", "EpicApp=PalServer" },
            new[]{ @"{root}\Pal\Saved" }, null,
            new GameCapabilities(ManagedConfig: true, Mods: true, Rcon: true, PlayerManagement: true, ConsoleMode: GameConsoleMode.Rcon),
            Notes: "RCON is optional. Enable it in Palworld server settings, then fill port/password."),

        new("satisfactory", "Satisfactory", "⚙", GameType.SteamGeneric, "1690800", "Satisfactory", "FactoryServer.exe",
            new[]{ "-log", "-unattended" },
            new[]{ @"C:\Users\Administrator\AppData\Local\FactoryGame\Saved\SaveGames" }, null,
            new GameCapabilities(ManagedConfig: true, Mods: true),
            Notes: "Satisfactory uses its native Windows save location by default."),

        new("zomboid", "Project Zomboid", "🧟", GameType.SteamGeneric, "380870", "ProjectZomboid", "StartServer64.bat",
            Array.Empty<string>(), new[]{ @"C:\Users\Administrator\Zomboid" }, null, Basic,
            Notes: "Project Zomboid uses the Windows user profile by default."),

        new("vrising", "V Rising", "🩸", GameType.SteamGeneric, "1829350", "VRising", "VRisingServer.exe",
            new[]{ "-persistentDataPath", @"{root}\data", "-serverName", "My V Rising Server", "-saveName", "world1", "-logFile", @"{root}\logs\VRisingServer.log" },
            new[]{ @"{root}\data" }, @"{root}\logs\VRisingServer.log",
            new GameCapabilities(LiveLog: true, ConsoleMode: GameConsoleMode.LogOnly)),

        new("conan", "Conan Exiles", "🗿", GameType.SteamGeneric, "443030", "Conan", "ConanSandboxServer.exe",
            new[]{ "-log", "-MULTIHOME=0.0.0.0", "-Port=7777", "-QueryPort=27015" },
            new[]{ @"{root}\ConanSandbox\Saved" }, null, Basic),

        new("squad", "Squad", "🎯", GameType.SteamGeneric, "403240", "Squad", "SquadGameServer.exe",
            new[]{ "-log", "-Port=7787", "-QueryPort=27165", "-MultiHome=0.0.0.0" },
            new[]{ @"{root}\SquadGame\Saved" }, null, Basic),

        new("soulmask", "Soulmask", "🗿", GameType.SteamGeneric, "3017310", "Soulmask", @"WS\Binaries\Win64\WSServer.exe",
            new[]{ "-log", "Level01_Main", "-server", "-SteamServerName=My Soulmask Server" },
            new[]{ @"{root}\WS\Saved" }, null, Basic),

        new("rust", "Rust", "🔧", GameType.SteamGeneric, "258550", "Rust", "RustDedicated.exe",
            new[]{ "-batchmode", "+server.port", "28015", "+rcon.port", "28016", "+rcon.password", "change-me", "+server.hostname", "My Rust Server" },
            new[]{ @"{root}\server" }, null, Basic,
            Notes: "The game supports RCON, but GameServerControl does not yet have a Rust RCON handler, so RCON controls stay hidden."),

        new("7dtd", "7 Days to Die", "🧟", GameType.SteamGeneric, "294420", "SevenDaysToDie", "7DaysToDieServer.exe",
            new[]{ "-quit", "-batchmode", "-nographics", "-configfile=serverconfig.xml" },
            new[]{ @"C:\Users\Administrator\AppData\Roaming\7DaysToDie\Saves" }, null, Basic,
            Notes: "Uses the game's native Windows save location until managed user-data relocation is implemented."),

        new("terraria", "Terraria", "🌳", GameType.SteamGeneric, "105600", "Terraria", "TerrariaServer.exe",
            new[]{ "-config", "serverconfig.txt" },
            new[]{ @"C:\Users\Administrator\Documents\My Games\Terraria\Worlds" }, null, Basic,
            Notes: "World location is controlled by Terraria's server config."),

        new("dst", "Don't Starve Together", "❄", GameType.SteamGeneric, "343050", "DST", @"bin\dontstarve_dedicated_server_nullrenderer.exe",
            new[]{ "-console", "-cluster", "MyCluster", "-shard", "Master" },
            new[]{ @"C:\Users\Administrator\Documents\Klei\DoNotStarveTogether" }, null, Basic,
            Notes: "Uses Klei's native user-profile storage by default."),

        new("minecraft", "Minecraft (Java)", "⛏", GameType.Minecraft, null, "Minecraft", "server.jar",
            new[]{ "java", "-Xmx4G", "-Xms4G", "-jar", "server.jar", "nogui" },
            new[]{ @"{root}\world", @"{root}\world_nether", @"{root}\world_the_end" }, @"{root}\logs\latest.log",
            new GameCapabilities(LiveLog: true, ConsoleMode: GameConsoleMode.LogOnly))
    };

    public static GameProfile? FindByKey(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static GameProfile? FindFor(ServerDef def)
        => All.FirstOrDefault(x => !string.IsNullOrWhiteSpace(def.SteamAppId) && x.SteamAppId == def.SteamAppId)
           ?? All.FirstOrDefault(x => x.GameType == def.GameType && x.GameType != GameType.SteamGeneric);
}
