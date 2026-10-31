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
/// User-facing game capabilities and safe defaults. Unsupported features are deliberately
/// absent from a profile so UIs do not expose backend implementation details to operators.
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
            Notes: "World data, admin lists and logs stay under the server folder."),

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
            Notes: "RCON is optional. Enable it in Palworld server settings, then enter its port and password."),

        new("satisfactory", "Satisfactory", "⚙", GameType.SteamGeneric, "1690800", "Satisfactory", "FactoryServer.exe",
            new[]{ "-log", "-unattended" },
            new[]{ @"C:\Windows\System32\config\systemprofile\AppData\Local\FactoryGame\Saved\SaveGames\server" },
            @"{root}\FactoryGame\Saved\Logs\FactoryGame.log",
            new GameCapabilities(ManagedConfig: true, Mods: true, LiveLog: true, ConsoleMode: GameConsoleMode.LogOnly),
            Notes: "Satisfactory does not document a save-root override. The default GameServerControl Windows service runs as LocalSystem, so saves remain in that service profile; logs are install-relative."),

        new("zomboid", "Project Zomboid", "🧟", GameType.SteamGeneric, "380870", "ProjectZomboid", "StartServer64.bat",
            new[]{ @"-cachedir={root}\data", "-servername", "GameServerControl" },
            new[]{ @"{root}\data" }, null, Basic,
            Notes: "-cachedir keeps configuration, saves and logs under the server folder."),

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
            Notes: "Rust supports RCON, but GameServerControl hides it until a Rust RCON handler is implemented."),

        new("7dtd", "7 Days to Die", "🧟", GameType.SteamGeneric, "294420", "SevenDaysToDie", "7DaysToDieServer.exe",
            new[]{ "-quit", "-batchmode", "-nographics", "-configfile=serverconfig.xml", @"-UserDataFolder={root}\data" },
            new[]{ @"{root}\data" }, null, Basic,
            Notes: "The supported -UserDataFolder override keeps generated worlds and saves under the server folder."),

        new("terraria", "Terraria", "🌳", GameType.SteamGeneric, "105600", "Terraria", "TerrariaServer.exe",
            new[]{ "-world", @"{root}\worlds\World.wld", "-autocreate", "2", "-worldname", "World", "-port", "7777", "-maxplayers", "8" },
            new[]{ @"{root}\worlds" }, null, Basic,
            Notes: "Creates/loads World.wld under the server folder instead of the Windows Documents profile."),

        new("dst", "Don't Starve Together", "❄", GameType.SteamGeneric, "343050", "DST", @"bin\dontstarve_dedicated_server_nullrenderer.exe",
            new[]{ "-console", "-persistent_storage_root", @"{root}\data", "-conf_dir", "DST", "-cluster", "MyCluster", "-shard", "Master" },
            new[]{ @"{root}\data\DST\MyCluster" }, null, Basic,
            Notes: "Klei's -persistent_storage_root override keeps the cluster under the server folder."),

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
