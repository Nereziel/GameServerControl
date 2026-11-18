using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Compares the observable server status, including metadata by value instead
/// of Dictionary reference identity. Status is still sampled and retained by
/// StatusTracker on each actual poll.
/// </summary>
public static class ServerStatusChangeDetector
{
    public static bool HasChanged(ServerStatus? previous, ServerStatus current)
    {
        if (previous is null) return true;
        if (previous.Id != current.Id ||
            previous.VmState != current.VmState ||
            previous.ProcessState != current.ProcessState ||
            previous.PidInGuest != current.PidInGuest ||
            previous.StartedAt != current.StartedAt ||
            previous.LastBackupAt != current.LastBackupAt ||
            previous.LastError != current.LastError ||
            previous.CpuPercent != current.CpuPercent ||
            previous.MemoryMB != current.MemoryMB)
            return true;

        if (ReferenceEquals(previous.Metadata, current.Metadata)) return false;
        if (previous.Metadata is null || current.Metadata is null) return true;
        if (previous.Metadata.Count != current.Metadata.Count) return true;
        return previous.Metadata.Any(item =>
            !current.Metadata.TryGetValue(item.Key, out var other) ||
            !StringComparer.Ordinal.Equals(item.Value, other));
    }
}
