using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class FakeWindowsRunnerServiceManager : IWindowsRunnerServiceManager
{
    public bool SupportsResult { get; set; } = true;
    public (bool Ok, string Error) DeprovisionResult { get; set; } = (true, "");
    public Func<string, bool>? ServerExists { get; set; }
    public int DeprovisionCalls { get; private set; }
    public int CompleteCalls { get; private set; }
    public bool? DeprovisionObservedServerPresent { get; private set; }
    public bool? CompleteObservedServerPresent { get; private set; }

    public bool Supports(ServerDef def) => SupportsResult;

    public Task<RunnerResponse> StartAsync(ServerDef def, CancellationToken ct = default) =>
        Task.FromResult(new RunnerResponse(true, Pid: 1234));

    public Task<RunnerResponse> FindAsync(ServerDef def, CancellationToken ct = default) =>
        Task.FromResult(new RunnerResponse(true));

    public Task<RunnerResponse> StopAsync(ServerDef def, bool force, CancellationToken ct = default) =>
        Task.FromResult(new RunnerResponse(true));

    public Task<RunnerResponse> ConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        Task.FromResult(new RunnerResponse(true, Console: new RunnerConsoleSnapshot(false, false, "fake", 0, Array.Empty<RunnerConsoleLine>())));

    public Task<RunnerResponse> PollConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        ConsoleStateAsync(def, ct);

    public Task<RunnerResponse> SendConsoleInputAsync(ServerDef def, string command, CancellationToken ct = default) =>
        Task.FromResult(new RunnerResponse(true));

    public Task<RunnerResponse> KillExtrasAsync(ServerDef def, CancellationToken ct = default) =>
        Task.FromResult(new RunnerResponse(true));

    public Task<bool?> GetAutostartAsync(ServerDef def, CancellationToken ct = default) =>
        Task.FromResult<bool?>(false);

    public Task<(bool Ok, string Error)> SetAutostartAsync(ServerDef def, bool enabled, CancellationToken ct = default) =>
        Task.FromResult((true, ""));

    public Task<(bool Ok, string Error)> DeprovisionAsync(ServerDef def, CancellationToken ct = default)
    {
        DeprovisionCalls++;
        DeprovisionObservedServerPresent = ServerExists?.Invoke(def.Id);
        return Task.FromResult(DeprovisionResult);
    }

    public void CompleteDeprovision(string serverId)
    {
        CompleteCalls++;
        CompleteObservedServerPresent = ServerExists?.Invoke(serverId);
    }
}
