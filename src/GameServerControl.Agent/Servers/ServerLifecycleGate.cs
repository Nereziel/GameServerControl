using System.Collections.Concurrent;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Single per-server gate for operations that change the game process or restore
/// its saved state. It is shared by the orchestrator and backup service.
/// Non-blocking acquisition preserves the API's existing busy response semantics.
/// </summary>
public sealed class ServerLifecycleGate
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<ActionResult> RunExclusiveAsync(
        string serverId, Func<Task<ActionResult>> operation, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            return new ActionResult(false,
                "Another Start, Stop, Restart, Update or Restore operation is already running for this server.",
                Guid.NewGuid().ToString("n"));

        try
        {
            return await operation();
        }
        finally
        {
            gate.Release();
        }
    }
}
