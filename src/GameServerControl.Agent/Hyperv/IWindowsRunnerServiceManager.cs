using GameServerControl.Shared;

namespace GameServerControl.Agent.Hyperv;

public interface IWindowsRunnerServiceManager
{
    bool Supports(ServerDef def);
    Task<RunnerResponse> StartAsync(ServerDef def, CancellationToken ct = default);
    Task<RunnerResponse> FindAsync(ServerDef def, CancellationToken ct = default);
    Task<RunnerResponse> StopAsync(ServerDef def, bool force, CancellationToken ct = default);
    Task<RunnerResponse> ConsoleStateAsync(ServerDef def, CancellationToken ct = default);
    Task<RunnerResponse> PollConsoleStateAsync(ServerDef def, string? consoleGeneration, long afterSequence, CancellationToken ct = default);
    Task<RunnerResponse> SendConsoleInputAsync(ServerDef def, string command, CancellationToken ct = default);
    Task<RunnerResponse> KillExtrasAsync(ServerDef def, CancellationToken ct = default);
    Task<bool?> GetAutostartAsync(ServerDef def, CancellationToken ct = default);
    Task<(bool Ok, string Error)> SetAutostartAsync(ServerDef def, bool enabled, CancellationToken ct = default);
    Task<(bool Ok, string Error)> DeprovisionAsync(ServerDef def, CancellationToken ct = default);
    void CompleteDeprovision(string serverId);
}
