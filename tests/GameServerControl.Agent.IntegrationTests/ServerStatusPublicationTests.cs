using System.Text.Json.Nodes;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ServerActionResult = GameServerControl.Shared.ActionResult;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ServerStatusPublicationTests
{
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Cancelled_refresh_does_not_publish_to_tracker_or_history()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Update("alpha", x => x with { ProcessState = ProcessState.Running, PidInGuest = 1234 });
        var before = fixture.Tracker.Get("alpha");
        var history = fixture.Tracker.History("alpha");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Orchestrator.RefreshStatusAsync("alpha", cancelled.Token));

        Assert.Equal(before, fixture.Tracker.Get("alpha"));
        Assert.Equal(history, fixture.Tracker.History("alpha"));
        Assert.DoesNotContain(fixture.Diagnostics.Snapshot("alpha").Events, x => x.Kind == "UnexpectedStop");
    }

    [Fact]
    public async Task Active_lifecycle_skips_background_and_rest_refresh_but_not_other_servers()
    {
        using var fixture = new Fixture();
        fixture.Tracker.Update("alpha", x => x with { ProcessState = ProcessState.Running, PidInGuest = 1234 });
        var before = fixture.Tracker.Get("alpha");
        var history = fixture.Tracker.History("alpha");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var action = fixture.Gate.RunExclusiveAsync("alpha", async () =>
        {
            entered.SetResult(true);
            await release.Task;
            return new ServerActionResult(true, "test", "test");
        });
        await entered.Task.WaitAsync(TestLimit);
        try
        {
            Assert.Null(await fixture.Orchestrator.RefreshStatusForMonitoringAsync("alpha").WaitAsync(TestLimit));
            Assert.Equal(before, await fixture.Orchestrator.RefreshStatusAsync("alpha").WaitAsync(TestLimit));
            Assert.Equal(history, fixture.Tracker.History("alpha"));
            Assert.NotNull(await fixture.Orchestrator.RefreshStatusForMonitoringAsync("beta").WaitAsync(TestLimit));
            Assert.False(await fixture.Gate.TryRunIdleAsync("alpha", () => throw new InvalidOperationException("Relay must skip busy server")));
        }
        finally
        {
            release.TrySetResult(true);
            Assert.True((await action.WaitAsync(TestLimit)).Success);
        }
    }

    [Fact]
    public async Task Late_windows_runner_response_cannot_publish_after_poll_timeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Tracker.Update("alpha", x => x with { ProcessState = ProcessState.Running, PidInGuest = 1234 });
        var before = fixture.Tracker.Get("alpha");
        var history = fixture.Tracker.History("alpha");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RunnerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Base.FakeRunner.FindOverride = async (_, token) =>
        {
            using var registration = token.Register(() => deadline.TrySetResult(true));
            entered.TrySetResult(true);
            // The provider intentionally ignores cancellation.
            return await release.Task;
        };
        var poller = new ServerStatusPoller(NullLogger<ServerStatusPoller>.Instance, 1, TimeSpan.FromMilliseconds(100));
        var batch = poller.PollAsync(new[] { "alpha" }, fixture.Orchestrator.RefreshStatusForMonitoringAsync);
        try
        {
            await entered.Task.WaitAsync(TestLimit);
            await deadline.Task.WaitAsync(TestLimit);
        }
        finally { release.TrySetResult(new RunnerResponse(true)); }

        Assert.Empty(await batch.WaitAsync(TestLimit));
        Assert.Equal(before, fixture.Tracker.Get("alpha"));
        Assert.Equal(history, fixture.Tracker.History("alpha"));
        Assert.DoesNotContain(fixture.Diagnostics.Snapshot("alpha").Events, x => x.Kind == "UnexpectedStop");
    }

    [Fact]
    public async Task Old_windows_probe_cannot_overwrite_real_stop_and_internal_refresh_does_not_deadlock()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.Tracker.Update("alpha", x => x with { ProcessState = ProcessState.Running, PidInGuest = 1234 });
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<RunnerResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Base.FakeRunner.FindOverride = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) != 1) return new RunnerResponse(true);
            entered.TrySetResult(true);
            return await release.Task;
        };
        var stale = fixture.Orchestrator.RefreshStatusForMonitoringAsync("alpha");
        try
        {
            await entered.Task.WaitAsync(TestLimit);
            var stopped = await fixture.Orchestrator.StopAsync("alpha", stopVm: false, force: true).WaitAsync(TestLimit);
            Assert.True(stopped.Success, stopped.Message);
            Assert.True(calls >= 2, "The lifecycle's internal refresh must execute, not skip the held gate.");
            Assert.Equal(ProcessState.NotRunning, fixture.Tracker.Get("alpha").ProcessState);
            var afterStop = fixture.Tracker.History("alpha");

            // Deliberately return the old running state after Stop completed.
            release.TrySetResult(new RunnerResponse(true, Pid: 1234));
            Assert.Null(await stale.WaitAsync(TestLimit));
            Assert.Equal(ProcessState.NotRunning, fixture.Tracker.Get("alpha").ProcessState);
            Assert.Null(fixture.Tracker.Get("alpha").PidInGuest);
            Assert.Equal(afterStop, fixture.Tracker.History("alpha"));
        }
        finally
        {
            release.TrySetResult(new RunnerResponse(true));
            await stale.WaitAsync(TestLimit);
        }
    }

    [Fact]
    public async Task Newer_observation_invalidates_older_same_server_observation()
    {
        var gate = new ServerLifecycleGate();
        var old = gate.TryBeginObservation("alpha")!.Value;
        var newer = gate.TryBeginObservation("ALPHA")!.Value;
        Assert.Null(await gate.TryPublishObservationAsync("alpha", old, () => Task.FromResult("stale")));
        Assert.Equal("fresh", await gate.TryPublishObservationAsync("alpha", newer, () => Task.FromResult("fresh")));
        var cancelled = gate.TryBeginObservation("alpha")!.Value;
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.TryPublishObservationAsync("alpha", cancelled, () => Task.FromResult("cancelled"), source.Token));
    }

    [Fact]
    public async Task Lifecycle_waits_for_accepted_relay_publication_without_returning_busy()
    {
        var gate = new ServerLifecycleGate();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifecycleEntered = false;
        var relay = gate.TryRunIdleAsync("alpha", async () =>
        {
            entered.SetResult(true);
            await release.Task;
        });
        await entered.Task.WaitAsync(TestLimit);
        var action = gate.RunExclusiveAsync("alpha", () =>
        {
            lifecycleEntered = true;
            return Task.FromResult(new ServerActionResult(true, "test", "test"));
        });
        try
        {
            Assert.False(action.IsCompleted);
            Assert.False(lifecycleEntered);
            Assert.Null(gate.TryBeginObservation("alpha"));
            Assert.False(await gate.TryRunIdleAsync("alpha", () => throw new InvalidOperationException("overlap")));
            Assert.True((await gate.RunExclusiveAsync("beta", () =>
                Task.FromResult(new ServerActionResult(true, "test", "test")))).Success);
        }
        finally
        {
            release.TrySetResult(true);
            Assert.True(await relay.WaitAsync(TestLimit));
            Assert.True((await action.WaitAsync(TestLimit)).Success);
        }
        Assert.True(lifecycleEntered);
    }

    private sealed class Fixture : IDisposable
    {
        public AgentTestFactory Base { get; } = new();
        private readonly WebApplicationFactory<global::GameServerControl.Agent.AgentEntryPoint> _factory;
        private readonly HttpClient _client;

        public Fixture()
        {
            // No outbound Discord calls in the fixture.
            var doc = JsonNode.Parse(File.ReadAllText(Base.ServersPath))!;
            foreach (var server in doc["Servers"]!.AsArray())
                server!["DiscordWebhookUrl"] = null;
            File.WriteAllText(Base.ServersPath, doc.ToJsonString());
            _factory = Base.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> {
                    ["Agent:DisableStatusMonitoring"] = "true"
                })));
            _client = _factory.CreateClient();
        }

        public StatusTracker Tracker => _factory.Services.GetRequiredService<StatusTracker>();
        public ServerDiagnostics Diagnostics => _factory.Services.GetRequiredService<ServerDiagnostics>();
        public ServerOrchestrator Orchestrator => _factory.Services.GetRequiredService<ServerOrchestrator>();
        public ServerLifecycleGate Gate => _factory.Services.GetRequiredService<ServerLifecycleGate>();

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
            Base.Dispose();
        }
    }
}
