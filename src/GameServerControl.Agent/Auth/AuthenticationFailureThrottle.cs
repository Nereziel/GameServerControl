using System.Collections.Concurrent;
using System.Globalization;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Bounded in-memory tracker for repeated authentication failures by direct peer address.
/// Successful authenticated requests clear that peer's failure state. Authorization failures
/// (403) are deliberately not counted because they represent a valid credential with a
/// restricted role rather than a failed authentication attempt.
/// </summary>
public sealed class AuthenticationFailureThrottle
{
    public const int MaxTrackedClients = 4096;

    private sealed class ClientState
    {
        public object Gate { get; } = new();
        public Queue<DateTimeOffset> Failures { get; } = new();
        public DateTimeOffset BlockedUntil { get; set; }
        public DateTimeOffset LastSeen { get; set; }
    }

    private readonly ConcurrentDictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _blockDuration;
    private long _operations;

    public AuthenticationFailureThrottle(IConfiguration cfg)
    {
        _maxFailures = ReadBoundedInt(cfg["Agent:AuthThrottle:MaxFailures"], fallback: 8, min: 2, max: 100);
        _window = TimeSpan.FromSeconds(ReadBoundedInt(cfg["Agent:AuthThrottle:WindowSeconds"], fallback: 60, min: 10, max: 3600));
        _blockDuration = TimeSpan.FromSeconds(ReadBoundedInt(cfg["Agent:AuthThrottle:BlockSeconds"], fallback: 60, min: 10, max: 3600));
    }

    public bool TryGetRetryAfter(string clientKey, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_clients.TryGetValue(clientKey, out var state)) return false;

        var remove = false;
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
            remove = state.Failures.Count == 0;
        }

        if (remove) _clients.TryRemove(new KeyValuePair<string, ClientState>(clientKey, state));
        return false;
    }

    public void RecordFailure(string clientKey, DateTimeOffset now)
    {
        CleanupIfNeeded(now);

        if (!_clients.TryGetValue(clientKey, out var state))
        {
            if (_clients.Count >= MaxTrackedClients) return;
            state = _clients.GetOrAdd(clientKey, _ => new ClientState { LastSeen = now });
        }

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
        => _clients.TryRemove(clientKey, out _);

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
            var stale = false;
            lock (state.Gate)
                stale = state.LastSeen < staleBefore && state.BlockedUntil <= now;
            if (stale) _clients.TryRemove(new KeyValuePair<string, ClientState>(pair.Key, state));
        }
    }

    private static int ReadBoundedInt(string? text, int fallback, int min, int max)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
}

public sealed class AuthenticationFailureThrottleMiddleware
{
    private readonly RequestDelegate _next;

    public AuthenticationFailureThrottleMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AuthenticationFailureThrottle throttle)
    {
        if (!IsProtectedPath(context.Request.Path) || context.Connection.RemoteIpAddress is null)
        {
            await _next(context);
            return;
        }

        var clientKey = context.Connection.RemoteIpAddress.ToString();
        var now = DateTimeOffset.UtcNow;
        if (throttle.TryGetRetryAfter(clientKey, now, out var retryAfter))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "Too many authentication failures. Try again later." });
            return;
        }

        await _next(context);

        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            throttle.RecordFailure(clientKey, DateTimeOffset.UtcNow);
        else if (context.User.Identity?.IsAuthenticated == true)
            throttle.RecordSuccess(clientKey);
    }

    private static bool IsProtectedPath(PathString path)
        => path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs");
}
