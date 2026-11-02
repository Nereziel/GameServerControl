using System.Collections.Concurrent;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed class StatusTracker
{
    public const int MaxHistoryPerServer = 240;

    private readonly ConcurrentDictionary<string, ServerStatus> _byId = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ServerStatusSample>> _history = new();

    public ServerStatus Get(string id) =>
        _byId.GetOrAdd(id, _ => new ServerStatus(id, VmState.Unknown, ProcessState.Unknown, null, null, null, null));

    public ServerStatus Update(string id, Func<ServerStatus, ServerStatus> updater)
    {
        var updated = _byId.AddOrUpdate(id,
            _ => updater(new ServerStatus(id, VmState.Unknown, ProcessState.Unknown, null, null, null, null)),
            (_, cur) => updater(cur));
        Record(updated);
        return updated;
    }

    public ServerStatusSample[] History(string id, int limit = 120)
    {
        if (!_history.TryGetValue(id, out var queue)) return Array.Empty<ServerStatusSample>();
        var bounded = Math.Clamp(limit, 1, MaxHistoryPerServer);
        return queue.ToArray().TakeLast(bounded).ToArray();
    }

    public IEnumerable<ServerStatus> Snapshot() => _byId.Values.ToArray();

    private void Record(ServerStatus status)
    {
        var queue = _history.GetOrAdd(status.Id, _ => new ConcurrentQueue<ServerStatusSample>());
        queue.Enqueue(new ServerStatusSample(
            DateTimeOffset.UtcNow,
            status.ProcessState,
            status.VmState,
            status.PidInGuest,
            status.CpuPercent,
            status.MemoryMB,
            status.LastError));
        while (queue.Count > MaxHistoryPerServer) queue.TryDequeue(out _);
    }
}
