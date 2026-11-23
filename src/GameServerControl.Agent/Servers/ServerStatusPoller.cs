using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Bounded, read-only status refreshes for different server identities.
/// A batch never launches concurrent polls of the same ID and completes all
/// active polls before the next cycle begins. Cancellation is cooperative:
/// we never detach an unfinished poll that could update status later.
/// </summary>
public sealed class ServerStatusPoller
{
    public const int DefaultMaxConcurrency = 4;
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromSeconds(4);

    public const string MeterName = "GameServerControl.Agent.Status";
    public const string PollDurationName = "gsc.status.poll.duration";
    public const string CycleDurationName = "gsc.status.cycle.duration";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> PollDuration =
        Meter.CreateHistogram<double>(PollDurationName, "ms", "Elapsed time for a single server status refresh");
    private static readonly Histogram<double> CycleDuration =
        Meter.CreateHistogram<double>(CycleDurationName, "ms", "Elapsed time for a complete status cycle including Relay");

    private readonly ILogger<ServerStatusPoller> _logger;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _timeout;

    public ServerStatusPoller(ILogger<ServerStatusPoller> logger)
        : this(logger, DefaultMaxConcurrency, DefaultPollTimeout) { }

    public ServerStatusPoller(ILogger<ServerStatusPoller> logger, int maxConcurrency, TimeSpan timeout)
    {
        if (maxConcurrency < 1 || maxConcurrency > 32)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _logger = logger;
        _maxConcurrency = maxConcurrency;
        _timeout = timeout;
    }

    public async Task<IReadOnlyDictionary<string, ServerStatus>> PollAsync(
        IEnumerable<string> serverIds,
        Func<string, CancellationToken, Task<ServerStatus>> refresh,
        CancellationToken stopping = default)
    {
        var ids = serverIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var updated = new ConcurrentDictionary<string, ServerStatus>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(ids, new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxConcurrency,
            CancellationToken = stopping
        }, async (id, ct) =>
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = "ok";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(_timeout);
            try
            {
                var status = await refresh(id, deadline.Token);
                ct.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested)
                {
                    // An external provider may ignore cancellation. Wait for it
                    // (never overlap another cycle), but reject its stale result.
                    outcome = "timeout";
                    _logger.LogWarning(
                        "Status poll for {ServerId} returned after its {TimeoutMs} ms deadline; stale result ignored.",
                        id, _timeout.TotalMilliseconds);
                }
                else
                {
                    updated[id] = status;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                outcome = "timeout";
                _logger.LogWarning("Status poll timed out for {ServerId} after {TimeoutMs} ms.",
                    id, _timeout.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                outcome = "error";
                _logger.LogWarning(ex, "Status poll failed for {ServerId}.", id);
            }
            finally
            {
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                PollDuration.Record(ms,
                    new KeyValuePair<string, object?>("server.id", id),
                    new KeyValuePair<string, object?>("result", outcome));
                _logger.LogDebug("Server {ServerId} status poll: {DurationMs:F0} ms ({Outcome})", id, ms, outcome);
                if (outcome == "ok" && ms > _timeout.TotalMilliseconds * 0.75)
                    _logger.LogWarning("Slow status refresh for {ServerId}: {DurationMs:F0} ms.", id, ms);
            }
        });
        return updated;
    }

    public void RecordCycle(TimeSpan elapsed, int registered, int attempted, bool syncedRelay)
    {
        CycleDuration.Record(elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("relay", syncedRelay));
        _logger.LogDebug("Status cycle: {DurationMs:F0} ms, {Polled}/{Total} servers, Relay sync: {Relay}",
            elapsed.TotalMilliseconds, attempted, registered, syncedRelay);
        if (elapsed > ServerStatusCadence.CycleInterval)
            _logger.LogWarning(
                "Status cycle exceeded {TargetMs:F0} ms: {DurationMs:F0} ms, {Polled}/{Total} servers, Relay sync: {Relay}.",
                ServerStatusCadence.CycleInterval.TotalMilliseconds, elapsed.TotalMilliseconds,
                attempted, registered, syncedRelay);
    }
}

/// <summary>
/// Wall-clock due times avoid stretching the 30-second Relay cadence when one
/// of the 5-second status iterations takes longer than normal.
/// </summary>
public sealed class ServerStatusCadence
{
    public static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RelayInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DiskInterval = TimeSpan.FromMinutes(1);

    private DateTimeOffset _nextRelay = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDisk = DateTimeOffset.MinValue;

    public bool ShouldSyncRelay(DateTimeOffset now)
    {
        if (now < _nextRelay) return false;
        _nextRelay = now + RelayInterval;
        return true;
    }

    public bool ShouldCheckDisk(DateTimeOffset now)
    {
        if (now < _nextDisk) return false;
        _nextDisk = now + DiskInterval;
        return true;
    }
}
