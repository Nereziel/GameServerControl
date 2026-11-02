using System.Collections.Concurrent;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Bounded in-memory tracker for repeated authentication failures by direct peer address.
/// Successful authentication clears that peer's failure state. Authorization failures are
/// deliberately outside this class because a valid ReadOnly credential must never count as
/// a failed authentication attempt.
/// </summary>
public sealed class AuthenticationFailureThrottle
{
    public const int MaxTrackedClients = 4096;
    public static AuthenticationFailureThrottle Shared { get; } = new();

    private sealed class ClientState
    {
        public object Gate { get; } = new();
        public Queue<DateTimeOffset> Failures { get; } = new();
        public DateTimeOffset BlockedUntil { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private readonly ConcurrentDictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private readonly object _clientCreationGate = new();
    private readonly int _maxFailures;
    private readonly int _maxTrackedClients;
    private readonly TimeSpan _window;
    private readonly TimeSpan _blockDuration;
    private long _operations;

    public AuthenticationFailureThrottle(
        int maxFailures = 8,
        TimeSpan? window = null,
        TimeSpan? blockDuration = null,
        int maxTrackedClients = MaxTrackedClients)
    {
        _maxFailures = Math.Clamp(maxFailures, 2, 100);
        _window = Clamp(window ?? TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
        _blockDuration = Clamp(blockDuration ?? TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), TimeSpan.FromHours(1));
        _maxTrackedClients = Math.Clamp(maxTrackedClients, 1, MaxTrackedClients);
    }

    public bool TryGetRetryAfter(string clientKey, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_clients.TryGetValue(clientKey, out var state)) return false;

        lock (state.Gate)
        {
            state.LastSeen = now;
            PruneFailures(state, now);

            if (state.BlockedUntil > now)
            {
                retryAfter = state.BlockedUntil - now;
                return true;
            }

            if (state.BlockedUntil != default)
                state.BlockedUntil = default;
        }

        return false;
    }

    public void RecordFailure(string clientKey, DateTimeOffset now)
    {
        CleanupIfNeeded(now);

        var state = GetOrCreateState(clientKey, now);
        if (state is null) return;

        lock (state.Gate)
        {
            state.LastSeen = now;
            PruneFailures(state, now);
            if (state.BlockedUntil > now) return;

            state.Failures.Enqueue(now);
            if (state.Failures.Count >= _maxFailures)
            {
                state.Failures.Clear();
                state.BlockedUntil = now + _blockDuration;
            }
        }
    }

    public void RecordSuccess(string clientKey)
    {
        if (!_clients.TryGetValue(clientKey, out var state)) return;
        lock (state.Gate)
        {
            state.Failures.Clear();
            state.BlockedUntil = default;
            state.LastSeen = DateTimeOffset.UtcNow;
        }
    }

    private ClientState? GetOrCreateState(string clientKey, DateTimeOffset now)
    {
        if (_clients.TryGetValue(clientKey, out var existing)) return existing;

        // Only first-seen clients take this lock. Serializing creation makes the
        // MaxTrackedClients limit a hard memory bound even under concurrent new IPs.
        lock (_clientCreationGate)
        {
            if (_clients.TryGetValue(clientKey, out existing)) return existing;
            if (_clients.Count >= _maxTrackedClients) return null;

            var created = new ClientState { LastSeen = now };
            return _clients.TryAdd(clientKey, created)
                ? created
                : (_clients.TryGetValue(clientKey, out existing) ? existing : null);
        }
    }

    private void PruneFailures(ClientState state, DateTimeOffset now)
    {
        var cutoff = now - _window;
        while (state.Failures.Count > 0 && state.Failures.Peek() < cutoff)
            state.Failures.Dequeue();
    }

    private void CleanupIfNeeded(DateTimeOffset now)
    {
        if ((Interlocked.Increment(ref _operations) & 127) != 0) return;
        var staleBefore = now - _window - _blockDuration;
        foreach (var pair in _clients)
        {
            var state = pair.Value;
            lock (state.Gate)
            {
                if (state.LastSeen < staleBefore && state.BlockedUntil <= now)
                    RemoveExact(pair.Key, state);
            }
        }
    }

    private bool RemoveExact(string clientKey, ClientState state)
        => ((ICollection<KeyValuePair<string, ClientState>>)_clients)
            .Remove(new KeyValuePair<string, ClientState>(clientKey, state));

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max)
        => value < min ? min : value > max ? max : value;
}
