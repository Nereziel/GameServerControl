using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ServerStatusPollingTests
{
    private readonly ITestOutputHelper _output;

    public ServerStatusPollingTests(ITestOutputHelper output) => _output = output;

    private static ServerStatus Status(string id) =>
        new(id, VmState.NotApplicable, ProcessState.Running, null, null, null, null);

    [Fact]
    public async Task Slow_and_unreachable_servers_are_polled_with_bounded_concurrency_and_deadlines()
    {
        var poller = new ServerStatusPoller(
            NullLogger<ServerStatusPoller>.Instance, maxConcurrency: 3,
            timeout: TimeSpan.FromMilliseconds(180));
        var ids = Enumerable.Range(0, 10).Select(i => "slow-" + i)
            .Concat(new[] { "unreachable-1", "unreachable-2" }).ToArray();
        var running = 0;
        var peak = 0;
        var started = Stopwatch.StartNew();

        var results = await poller.PollAsync(ids, async (id, ct) =>
        {
            var active = Interlocked.Increment(ref running);
            while (true)
            {
                var maximum = Volatile.Read(ref peak);
                if (active <= maximum || Interlocked.CompareExchange(ref peak, active, maximum) == maximum) break;
            }
            try
            {
                if (id.StartsWith("unreachable-", StringComparison.Ordinal))
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                else
                    await Task.Delay(TimeSpan.FromMilliseconds(65), ct);
                return Status(id);
            }
            finally { Interlocked.Decrement(ref running); }
        });

        started.Stop();
        _output.WriteLine("Synthetic 12-server batch: {0:F0} ms, peak concurrency {1}, successes {2}, timeouts {3}",
            started.Elapsed.TotalMilliseconds, peak, results.Count, ids.Length - results.Count);

        Assert.Equal(10, results.Count);
        Assert.Equal(0, running);
        Assert.InRange(peak, 2, 3);
        Assert.DoesNotContain("unreachable-1", results.Keys);
        Assert.DoesNotContain("unreachable-2", results.Keys);
        // The serial baseline is 10*65 + 2*180 = 1,010 ms
        // (excluding processing/queue delays), whereas this batch overlaps
        // different servers; allow ample CI scheduling overhead.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3),
            "The bounded synthetic polling batch must finish without waiting indefinitely.");
    }

    [Fact]
    public async Task Duplicate_server_id_is_never_refreshed_in_parallel()
    {
        var poller = new ServerStatusPoller(NullLogger<ServerStatusPoller>.Instance,
            maxConcurrency: 4, timeout: TimeSpan.FromSeconds(1));
        var hits = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var results = await poller.PollAsync(
            new[] { "alpha", "Alpha", "ALPHA", "beta", "BETA" },
            async (id, ct) =>
            {
                hits.AddOrUpdate(id, 1, (_, count) => count + 1);
                await Task.Delay(20, ct);
                return Status(id);
            });
        Assert.Equal(2, results.Count);
        Assert.Equal(2, hits.Count);
        Assert.All(hits, pair => Assert.Equal(1, pair.Value));
    }

    [Fact]
    public async Task Noncooperative_timeout_must_be_awaited_and_its_late_status_discarded()
    {
        var poller = new ServerStatusPoller(NullLogger<ServerStatusPoller>.Instance,
            maxConcurrency: 1, timeout: TimeSpan.FromMilliseconds(30));
        var finished = false;
        var stopwatch = Stopwatch.StartNew();
        var results = await poller.PollAsync(new[] { "slow" }, async (id, _) =>
        {
            // A fake external provider ignores the cancellation token.
            await Task.Delay(120);
            finished = true;
            return Status(id);
        });

        Assert.True(finished, "The next status cycle must not begin while an old poll remains in flight.");
        Assert.Empty(results);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task Stopping_the_agent_cancels_inflight_polls_without_starting_another_cycle()
    {
        var poller = new ServerStatusPoller(NullLogger<ServerStatusPoller>.Instance,
            maxConcurrency: 2, timeout: TimeSpan.FromSeconds(10));
        using var source = new CancellationTokenSource();
        var running = 0;
        var batch = poller.PollAsync(Enumerable.Range(0, 8).Select(i => "server-" + i),
            async (id, ct) =>
            {
                Interlocked.Increment(ref running);
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                finally { Interlocked.Decrement(ref running); }
                return Status(id);
            }, source.Token);

        await Task.Delay(40);
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await batch; });
        Assert.Equal(0, running);
    }

    [Fact]
    public void Five_second_poll_fifteen_second_idle_and_thirty_second_relay_cadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ServerStatusCadence.CycleInterval);
        Assert.Equal(TimeSpan.FromSeconds(15), ServerStatusPollPolicy.StoppedInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), ServerStatusCadence.RelayInterval);

        var policy = new ServerStatusPollPolicy();
        var cadence = new ServerStatusCadence();
        var baseTime = new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
        var running = 0;
        var stopped = 0;
        var relay = 0;
        var disk = 0;
        for (var i = 0; i < 12; i++)
        {
            var now = baseTime + i * ServerStatusCadence.CycleInterval;
            if (policy.ShouldPoll("online", ProcessState.Running, now)) running++;
            if (policy.ShouldPoll("offline", ProcessState.NotRunning, now)) stopped++;
            if (cadence.ShouldSyncRelay(now)) relay++;
            if (cadence.ShouldCheckDisk(now)) disk++;
        }

        Assert.Equal(12, running); // 0,5,...55 seconds
        Assert.Equal(4, stopped); // 0,15,30,45 seconds
        Assert.Equal(2, relay); // 0,30 seconds
        Assert.Equal(1, disk); // 0 seconds

        // Slow work may skip a five-second tick. Relay scheduling is based
        // on elapsed wall clock, not statusCycle % 6.
        Assert.True(cadence.ShouldSyncRelay(baseTime.AddSeconds(78)));
        Assert.False(cadence.ShouldSyncRelay(baseTime.AddSeconds(79)));
        Assert.True(cadence.ShouldSyncRelay(baseTime.AddSeconds(108)));
        Assert.True(cadence.ShouldCheckDisk(baseTime.AddSeconds(78)));
        Assert.False(cadence.ShouldCheckDisk(baseTime.AddSeconds(79)));
    }

    [Fact]
    public async Task Histogram_metrics_observe_per_server_and_whole_cycle_with_no_secrets()
    {
        var measurements = new ConcurrentBag<(string Name, double Value, string Labels)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == ServerStatusPoller.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var label = string.Join(",", tags.ToArray().Select(t => t.Key + "=" + t.Value));
            measurements.Add((instrument.Name, value, label));
        });
        listener.Start();

        var poller = new ServerStatusPoller(NullLogger<ServerStatusPoller>.Instance);
        await poller.PollAsync(new[] { "alpha" },
            (id, ct) => Task.FromResult(Status(id)));
        poller.RecordCycle(TimeSpan.FromMilliseconds(42), registered: 2, attempted: 1, syncedRelay: true);

        Assert.Contains(measurements, x => x.Name == ServerStatusPoller.PollDurationName
            && x.Value >= 0 && x.Labels.Contains("server.id=alpha", StringComparison.Ordinal));
        Assert.Contains(measurements, x => x.Name == ServerStatusPoller.CycleDurationName
            && x.Value == 42 && x.Labels.Contains("relay=True", StringComparison.OrdinalIgnoreCase));
    }
}
