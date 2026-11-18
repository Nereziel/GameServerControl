using System.Collections.Concurrent;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Suppresses repeated operational alerts until a condition recovers.
/// A relay warning requires two consecutive failed checks (~one minute).
/// </summary>
public sealed class ServerAlertPolicy
{
    private sealed class State
    {
        public readonly object Gate = new();
        public int ConsecutiveRelayFailures;
        public bool RelayWarned;
        public bool DiskWarned;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    public const long LowDiskBytes = 5L * 1024 * 1024 * 1024;
    public const long DiskRecoveryBytes = 8L * 1024 * 1024 * 1024;

    public bool ReportRelay(string serverId, bool enabledAndRunning, bool healthy)
    {
        var state = _states.GetOrAdd(serverId, _ => new State());
        lock (state.Gate)
        {
            if (!enabledAndRunning || healthy)
            {
                state.ConsecutiveRelayFailures = 0;
                state.RelayWarned = false;
                return false;
            }

            state.ConsecutiveRelayFailures++;
            if (state.ConsecutiveRelayFailures < 2 || state.RelayWarned) return false;
            state.RelayWarned = true;
            return true;
        }
    }

    public bool ReportFreeDisk(string serverId, long? availableBytes)
    {
        if (availableBytes is null || availableBytes < 0) return false;
        var state = _states.GetOrAdd(serverId, _ => new State());
        lock (state.Gate)
        {
            if (availableBytes >= DiskRecoveryBytes) state.DiskWarned = false;
            if (availableBytes > LowDiskBytes || state.DiskWarned) return false;
            state.DiskWarned = true;
            return true;
        }
    }

    // Passive host-filesystem inspection. No network scans or remote disk probes.
    public static long? AvailableDiskBytes(string serverDirectory)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory)) return null;
        try
        {
            var full = Path.GetFullPath(serverDirectory);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives().Where(d => d.IsReady)
                .OrderByDescending(d => d.RootDirectory.FullName.Length)
                .FirstOrDefault(d =>
                {
                    var root = d.RootDirectory.FullName;
                    return full.Equals(root.TrimEnd(Path.DirectorySeparatorChar), comparison)
                        || full.StartsWith(root, comparison);
                });
            return drive?.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
