using System.Collections.Concurrent;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Per-server lifecycle gate and optimistic status observation fence.
/// Read-only probes do not hold the lifecycle gate. Starting a lifecycle
/// operation invalidates earlier observations and prevents new publications.
/// Only the short publication/Relay phase is serialized with lifecycle work.
/// A monitor cannot cause the ordinary lifecycle busy response.
/// </summary>
public sealed class ServerLifecycleGate
{
    private sealed class State
    {
        public readonly SemaphoreSlim Lifecycle = new(1, 1);
        public readonly SemaphoreSlim Publication = new(1, 1);
        public readonly object Sync = new();
        public long Version;
        public bool LifecyclePending;
    }

    private readonly ConcurrentDictionary<string, State> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    private State For(string id) => _gates.GetOrAdd(id, _ => new State());

    public async Task<ActionResult> RunExclusiveAsync(
        string serverId, Func<Task<ActionResult>> operation, CancellationToken ct = default)
    {
        var state = For(serverId);
        if (!await state.Lifecycle.WaitAsync(0, ct))
            return new ActionResult(false,
                "Another Start, Stop, Restart, Update or Restore operation is already running for this server.",
                Guid.NewGuid().ToString("n"));

        lock (state.Sync)
        {
            state.LifecyclePending = true;
            state.Version++;
        }

        var entered = false;
        try
        {
            // A publication already in progress finishes before mutation.
            // No potentially slow status probe is awaited here.
            await state.Publication.WaitAsync(ct);
            entered = true;
            ct.ThrowIfCancellationRequested();
            return await operation();
        }
        finally
        {
            lock (state.Sync)
            {
                state.Version++;
                state.LifecyclePending = false;
            }
            if (entered) state.Publication.Release();
            state.Lifecycle.Release();
        }
    }

    public long? TryBeginObservation(string serverId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var state = For(serverId);
        lock (state.Sync)
        {
            if (state.LifecyclePending) return null;
            // A newer REST or background observation supersedes an older one.
            return ++state.Version;
        }
    }

    public Task<T?> TryPublishObservationAsync<T>(
        string serverId, long version, Func<Task<T>> publish, CancellationToken ct = default)
        where T : class
        => TryRunIdleCoreAsync(serverId, version, publish, ct);

    // Relay reads the current tracker state INSIDE this boundary. The
    // lifecycle semaphore is separate, so a user action may wait for an
    // already running reconciliation but is never rejected as busy by it.
    public async Task<bool> TryRunIdleAsync(
        string serverId, Func<Task> operation, CancellationToken ct = default)
        => await TryRunIdleCoreAsync(serverId, null, async () =>
        {
            await operation();
            return new object();
        }, ct) is not null;

    private async Task<T?> TryRunIdleCoreAsync<T>(
        string id, long? version, Func<Task<T>> operation, CancellationToken ct)
        where T : class
    {
        var state = For(id);
        if (!await state.Publication.WaitAsync(0, ct)) return null;
        try
        {
            lock (state.Sync)
            {
                if (state.LifecyclePending || (version.HasValue && state.Version != version.Value))
                    return null;
                ct.ThrowIfCancellationRequested();
            }
            // Linearization point: an accepted publication owns Publication
            // until all its effects finish. Later lifecycle work waits for it.
            return await operation();
        }
        finally { state.Publication.Release(); }
    }
}
