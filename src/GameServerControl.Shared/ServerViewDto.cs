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
    int? BackupRetention,
    string? GameProfileKey,
    bool AutoUpdateOnStart,
    bool AutoUpdateOnRestart)
{
    public static ServerViewDto From(ServerDef def) => new(
        def.Id,
        def.Name,
        def.VmName,
        def.GameType,
        "", // Host executable and install paths are Admin-only.
        "",
        // Launch arguments can contain game/admin passwords (Valheim, ARK, etc.).
        // Never expose them to ReadOnly clients.
        Array.Empty<string>(),
        Array.Empty<string>(),
        def.SteamAppId,
        null,
        null,
        null,
        def.RconPort,
        !string.IsNullOrEmpty(def.RconPassword),
        def.HostingMode,
        null,
        null,
        !string.IsNullOrEmpty(def.DiscordWebhookUrl),
        def.NetworkPorts,
        def.BackupRetention,
        def.GameProfileKey,
        def.AutoUpdateOnStart,
        def.AutoUpdateOnRestart);
}
