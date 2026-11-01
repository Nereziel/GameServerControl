using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Adds profile settings that do not belong in the large static schema table. The class name is
/// retained for DI/back-compat, but it now supplements both Dragonwilds and Valheim.
/// </summary>
public sealed class DragonwildsDynamicSchema : IDynamicSchemaExtension
{
    private readonly DragonwildsConfig _dragonwilds;
    private readonly ValheimConfig _valheim;

    public DragonwildsDynamicSchema(DragonwildsConfig dragonwilds, ValheimConfig valheim)
    {
        _dragonwilds = dragonwilds;
        _valheim = valheim;
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && (def.SteamAppId == "4019830" || def.SteamAppId == "896660");

    public async Task<DynamicSchemaResult?> BuildAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct)
    {
        if (def.SteamAppId == "896660")
            return await BuildValheimAsync(def, curatedKeys, ct);
        return await BuildDragonwildsAsync(def, curatedKeys, ct);
    }

    private async Task<DynamicSchemaResult> BuildDragonwildsAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct)
    {
        var values = await _dragonwilds.ReadAsync(def, ct);
        var fields = new[]
        {
            new ConfigField("OwnerId", "Owner Player ID", ConfigFieldType.Text,
                Description: "Required by Jagex's current self-hosting guide. Copy your Dragonwilds Player ID from the bottom of the in-game Settings screen."),
            new ConfigField("ServerName", "Server name", ConfigFieldType.Text,
                Description: "Required. Name shown for this dedicated server."),
            new ConfigField("DefaultWorldName", "Default world name", ConfigFieldType.Text,
                Description: "Required. Used when the server creates a new world."),
            new ConfigField("AdminPassword", "Admin password", ConfigFieldType.Password,
                Description: "Required. Grants access to the in-game Server Management screen."),
            new ConfigField("WorldPassword", "World password", ConfigFieldType.Password,
                Description: "Optional. Overrides the password stored in the selected world."),
            new ConfigField("Port", "Game port", ConfigFieldType.Integer,
                Description: "UDP listen port. 7777 is the first/default instance; use 7778, 7779, … for additional local instances.",
                Default: "7777", Min: 1, Max: 65535),
            new ConfigField("PlatformPolicy", "Crossplay / platform policy", ConfigFieldType.Choice,
                Description: "Dragonwilds 1.0 dedicated-server platform policy. Crossplay accepts all supported platforms; platform-specific values restrict joins.",
                Choices: new[] { "Crossplay", "PC", "PlayStation", "Xbox", "Nintendo" }, Default: "Crossplay")
        }
        .Where(f => !curatedKeys.Contains(f.Key))
        .ToArray();

        return new DynamicSchemaResult(
            new ConfigSection("RuneScape: Dragonwilds server settings", fields),
            values);
    }

    private async Task<DynamicSchemaResult> BuildValheimAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct)
    {
        var values = await _valheim.ReadAsync(def, ct);
        var fields = new[]
        {
            new ConfigField("backupshort", "Short backup interval (sec)", ConfigFieldType.Integer,
                Description: "Interval for Valheim's first/short automatic backup. Official default: 7200 seconds (2 hours).",
                Default: "7200", Min: 60, Max: 604800),
            new ConfigField("backuplong", "Long backup interval (sec)", ConfigFieldType.Integer,
                Description: "Interval between subsequent automatic backups. Official default: 43200 seconds (12 hours).",
                Default: "43200", Min: 60, Max: 2592000),
            new ConfigField("instanceid", "Crossplay instance ID", ConfigFieldType.Text,
                Description: "Optional unique ID for multiple Valheim servers using the same port/MAC address so PlayFab assigns distinct server identities."),
            new ConfigField("adminlist", "Administrators", ConfigFieldType.Multiline,
                Description: "One case-sensitive Platform User ID per line. These users may use Valheim's in-game server admin commands."),
            new ConfigField("bannedlist", "Banned players", ConfigFieldType.Multiline,
                Description: "One case-sensitive Platform User ID per line. Stored in bannedlist.txt under the configured save directory."),
            new ConfigField("permittedlist", "Permitted players", ConfigFieldType.Multiline,
                Description: "One case-sensitive Platform User ID per line. Warning: once this list contains anyone, everybody not listed is blocked from joining.")
        }
        .Where(f => !curatedKeys.Contains(f.Key))
        .ToArray();

        return new DynamicSchemaResult(
            new ConfigSection("Valheim advanced server access & backups", fields),
            values);
    }
}
