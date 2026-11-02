namespace GameServerControl.Shared;

/// <summary>
/// Browser-safe projection of a server definition. Secret values intentionally never appear here.
/// </summary>
public sealed record ServerViewDto(
    string Id,
    string Name,
    string VmName,
    GameType GameType,
    string GuestExePath,
    string GuestWorkingDir,
    string[] StartArgs,
    string[] SaveDirs,
    string? SteamAppId,
    string? GuestCredentialId,
    string? LogPathInGuest,
    string? RconHost,
    int? RconPort,
    bool HasRconPassword,
    HostingMode HostingMode,
    string? ScheduledTaskName,
    string[]? StopProcessNames,
    bool HasDiscordWebhookUrl,
    NetworkPort[]? NetworkPorts,
    int? BackupRetention)
{
    public static ServerViewDto From(ServerDef def) => new(
        def.Id,
        def.Name,
        def.VmName,
        def.GameType,
        def.GuestExePath,
        def.GuestWorkingDir,
        def.StartArgs,
        def.SaveDirs,
        def.SteamAppId,
        def.GuestCredentialId,
        def.LogPathInGuest,
        def.RconHost,
        def.RconPort,
        !string.IsNullOrEmpty(def.RconPassword),
        def.HostingMode,
        def.ScheduledTaskName,
        def.StopProcessNames,
        !string.IsNullOrEmpty(def.DiscordWebhookUrl),
        def.NetworkPorts,
        def.BackupRetention);
}
