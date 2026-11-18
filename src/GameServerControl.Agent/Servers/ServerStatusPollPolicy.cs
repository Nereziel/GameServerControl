using System.Collections.Concurrent;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Keep running and uncertain game states responsive while reducing idle Runner
/// IPC and process enumeration for servers already known to be stopped.
/// </summary>
public sealed class ServerStatusPollPolicy
{
    public static readonly TimeSpan StoppedInterval = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastStoppedPoll = new(StringComparer.OrdinalIgnoreCase);

    public bool ShouldPoll(string serverId, ProcessState state, DateTimeOffset now)
    {
        if (state != ProcessState.NotRunning)
        {
            _lastStoppedPoll.TryRemove(serverId, out _);
            return true;
        }

        if (_lastStoppedPoll.TryGetValue(serverId, out var last) &&
            now - last < StoppedInterval && now >= last)
            return false;

        _lastStoppedPoll[serverId] = now;
        return true;
    }
}
