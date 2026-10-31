using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Provides the curated Dragonwilds server settings without requiring a large static
/// schema entry in Shared/ConfigSchema.cs. The existing config endpoint composes this
/// section automatically and the standard editor renders it unchanged.
/// </summary>
public sealed class DragonwildsDynamicSchema : IDynamicSchemaExtension
{
    private readonly DragonwildsConfig _config;

    public DragonwildsDynamicSchema(DragonwildsConfig config)
    {
        _config = config;
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && def.SteamAppId == "4019830";

    public async Task<DynamicSchemaResult?> BuildAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct)
    {
        var values = await _config.ReadAsync(def, ct);
        var fields = new[]
        {
            new ConfigField("OwnerId", "Owner Player ID", ConfigFieldType.Text,
                Description: "Required. Your Dragonwilds Player ID from the bottom of the in-game Settings screen."),
            new ConfigField("ServerName", "Server name", ConfigFieldType.Text,
                Description: "Required. Name of this dedicated server."),
            new ConfigField("DefaultWorldName", "Default world name", ConfigFieldType.Text,
                Description: "Required. Used when the server creates a new world. Keep it short and easy to search."),
            new ConfigField("AdminPassword", "Admin password", ConfigFieldType.Password,
                Description: "Required. Grants access to the in-game Server Management screen."),
            new ConfigField("WorldPassword", "World password", ConfigFieldType.Password,
                Description: "Optional. Leave blank for a world that does not require a join password."),
        }
        .Where(f => !curatedKeys.Contains(f.Key))
        .ToArray();

        return new DynamicSchemaResult(
            new ConfigSection("RuneScape: Dragonwilds server settings", fields),
            values);
    }
}
