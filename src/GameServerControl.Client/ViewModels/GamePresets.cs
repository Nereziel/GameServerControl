using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed record GamePreset(
    string Key,
    string Label,
    GameType GameType,
    string? SteamAppId,
    string DefaultExeRelative,
    string DefaultWorkingDir,
    string[] DefaultStartArgs,
    string[] DefaultSaveDirs,
    string? DefaultLogPath,
    GameCapabilities Capabilities,
    int? DefaultRconPort,
    string? Notes);

public static class GamePresets
{
    public static readonly GamePreset[] All = GameProfiles.All.Select(ToPreset).ToArray();

    public static GamePreset? FindByKey(string key)
        => All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public static GamePreset? FindFor(ServerDef def)
    {
        var profile = GameProfiles.FindFor(def);
        return profile is null ? null : ToPreset(profile);
    }

    private static GamePreset ToPreset(GameProfile p)
    {
        var root = p.DefaultWindowsInstallPath;
        return new GamePreset(
            p.Key,
            p.Label,
            p.GameType,
            p.SteamAppId,
            p.ExecutableRelative,
            root,
            p.BuildStartArgs(root),
            p.BuildSaveDirs(root),
            p.BuildLogPath(root),
            p.Capabilities,
            p.DefaultRconPort,
            p.Notes);
    }
}
