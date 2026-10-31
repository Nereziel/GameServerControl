using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

public sealed class GameConfigFactory
{
    private readonly IServiceProvider _sp;
    public GameConfigFactory(IServiceProvider sp) { _sp = sp; }

    public IGameConfig? For(ServerDef def)
    {
        // Dragonwilds exposes its schema through a dynamic extension, so handle it
        // before consulting the static ConfigSchemas table.
        if (def.GameType == GameType.SteamGeneric && def.SteamAppId == "4019830")
            return _sp.GetRequiredService<DragonwildsConfig>();

        var schema = ConfigSchemas.For(def);
        return schema?.Key switch
        {
            "valheim"      => _sp.GetRequiredService<ValheimConfig>(),
            "palworld"     => _sp.GetRequiredService<PalworldConfig>(),
            "windrose"     => _sp.GetRequiredService<WindroseConfig>(),
            "satisfactory" => _sp.GetRequiredService<SatisfactoryConfig>(),
            _ => null
        };
    }
}
