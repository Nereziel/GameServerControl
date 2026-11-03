using System.Net;
using System.Net.Http.Json;
using GameServerControl.Agent.Logs;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class SignalRIsolationIntegrationTests
{
    [Fact]
    public async Task Log_tail_reaches_only_the_subscribed_server_group()
    {
        using var factory = new AgentTestFactory();
        var readOnlyToken = factory.CreateReadOnlyToken();
        await using var alpha = factory.CreateHubConnection(readOnlyToken);
        await using var beta = factory.CreateHubConnection(readOnlyToken);
        using var client = factory.CreateClientWithToken(readOnlyToken);

        var liveStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var alphaSentinel = new TaskCompletionSource<LogLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaSentinel = new TaskCompletionSource<LogLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentinel = "integration-sentinel-" + Guid.NewGuid().ToString("N");

        alpha.On<LogLine>("logLine", line =>
        {
            if (line.ServerId == "alpha" && line.Text.Contains("[live log started", StringComparison.Ordinal))
                liveStarted.TrySetResult(true);
            if (line.ServerId == "alpha" && line.Text == sentinel)
                alphaSentinel.TrySetResult(line);
        });
        beta.On<LogLine>("logLine", line =>
        {
            if (line.Text == sentinel)
                betaSentinel.TrySetResult(line);
        });

        await alpha.StartAsync();
        await beta.StartAsync();
        await alpha.InvokeAsync("SubscribeConsole", "alpha");
        await beta.InvokeAsync("SubscribeConsole", "beta");

        try
        {
            using var started = await client.PostAsJsonAsync("/api/servers/alpha/logs/tail", new { action = "start" });
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);

            await liveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.AppendAllTextAsync(factory.AlphaLogPath, sentinel + Environment.NewLine);

            var delivered = await alphaSentinel.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal("alpha", delivered.ServerId);
            Assert.Equal(sentinel, delivered.Text);

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.False(betaSentinel.Task.IsCompleted, "beta subscriber received alpha log traffic");
        }
        finally
        {
            try { await client.PostAsJsonAsync("/api/servers/alpha/logs/tail", new { action = "stop" }); } catch { }
            try { await alpha.StopAsync(); } catch { }
            try { await beta.StopAsync(); } catch { }
        }
    }

    [Fact]
    public async Task Repeated_log_tail_start_does_not_replay_recent_backlog()
    {
        using var factory = new AgentTestFactory(alphaUsesLogProfile: true);
        var history = "history-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(factory.AlphaLogPath, history + Environment.NewLine);

        var readOnlyToken = factory.CreateReadOnlyToken();
        await using var connection = factory.CreateHubConnection(readOnlyToken);
        using var client = factory.CreateClientWithToken(readOnlyToken);
        var received = new List<LogLine>();
        var gate = new object();

        connection.On<LogLine>("logLine", line =>
        {
            if (line.ServerId != "alpha") return;
            lock (gate) received.Add(line);
        });

        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeConsole", "alpha");

        try
        {
            using var firstStart = await client.PostAsJsonAsync("/api/servers/alpha/logs/tail", new { action = "start" });
            Assert.Equal(HttpStatusCode.OK, firstStart.StatusCode);

            await WaitUntilAsync(() =>
            {
                lock (gate) return received.Count(line => line.Text == history) >= 1;
            }, TimeSpan.FromSeconds(5));

            int beforeSecondStart;
            lock (gate) beforeSecondStart = received.Count(line => line.Text == history);
            Assert.Equal(1, beforeSecondStart);

            using var secondStart = await client.PostAsJsonAsync("/api/servers/alpha/logs/tail", new { action = "start" });
            Assert.Equal(HttpStatusCode.OK, secondStart.StatusCode);

            await Task.Delay(TimeSpan.FromSeconds(1));

            int afterSecondStart;
            lock (gate) afterSecondStart = received.Count(line => line.Text == history);
            Assert.Equal(beforeSecondStart, afterSecondStart);
        }
        finally
        {
            try { await client.PostAsJsonAsync("/api/servers/alpha/logs/tail", new { action = "stop" }); } catch { }
            try { await connection.StopAsync(); } catch { }
        }
    }

    [Fact]
    public async Task LogOnly_subscription_delivers_recent_backlog_once_per_connection()
    {
        using var factory = new AgentTestFactory(alphaUsesLogProfile: true);
        var history = "caller-history-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(factory.AlphaLogPath, history + Environment.NewLine);

        var readOnlyToken = factory.CreateReadOnlyToken();
        await using var first = factory.CreateHubConnection(readOnlyToken);
        await using var second = factory.CreateHubConnection(readOnlyToken);
        var firstReceived = new List<LogLine>();
        var secondReceived = new List<LogLine>();
        var firstGate = new object();
        var secondGate = new object();

        first.On<LogLine>("logLine", line =>
        {
            if (line.ServerId != "alpha") return;
            lock (firstGate) firstReceived.Add(line);
        });
        second.On<LogLine>("logLine", line =>
        {
            if (line.ServerId != "alpha") return;
            lock (secondGate) secondReceived.Add(line);
        });

        await first.StartAsync();
        await second.StartAsync();
        try
        {
            await first.InvokeAsync("SubscribeConsole", "alpha");
            await WaitUntilAsync(() =>
            {
                lock (firstGate) return firstReceived.Count(line => line.Text == history) == 1;
            }, TimeSpan.FromSeconds(5));

            await first.InvokeAsync("SubscribeConsole", "alpha");
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            lock (firstGate)
                Assert.Equal(1, firstReceived.Count(line => line.Text == history));

            await second.InvokeAsync("SubscribeConsole", "alpha");
            await WaitUntilAsync(() =>
            {
                lock (secondGate) return secondReceived.Count(line => line.Text == history) == 1;
            }, TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { await first.StopAsync(); } catch { }
            try { await second.StopAsync(); } catch { }
        }
    }

    [Fact]
    public async Task LogOnly_live_tail_survives_other_subscriber_disconnect_and_stops_after_last_disconnect()
    {
        using var factory = new AgentTestFactory(alphaUsesLogProfile: true);
        var firstToken = factory.CreateReadOnlyToken();
        var secondToken = factory.CreateReadOnlyToken();
        await using var first = factory.CreateHubConnection(firstToken);
        await using var second = factory.CreateHubConnection(secondToken);
        var tail = factory.Services.GetRequiredService<LogTailService>();
        var firstSentinel = "first-live-" + Guid.NewGuid().ToString("N");
        var secondSentinel = "second-live-" + Guid.NewGuid().ToString("N");
        var firstSawFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSawFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSawSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        first.On<LogLine>("logLine", line =>
        {
            if (line.ServerId == "alpha" && line.Text == firstSentinel)
                firstSawFirst.TrySetResult(true);
        });
        second.On<LogLine>("logLine", line =>
        {
            if (line.ServerId != "alpha") return;
            if (line.Text == firstSentinel) secondSawFirst.TrySetResult(true);
            if (line.Text == secondSentinel) secondSawSecond.TrySetResult(true);
        });

        await first.StartAsync();
        await second.StartAsync();
        try
        {
            await first.InvokeAsync("SubscribeConsole", "alpha");
            await second.InvokeAsync("SubscribeConsole", "alpha");
            await WaitUntilAsync(() => tail.IsTailing("alpha"), TimeSpan.FromSeconds(5));

            await File.AppendAllTextAsync(factory.AlphaLogPath, firstSentinel + Environment.NewLine);
            await firstSawFirst.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await secondSawFirst.Task.WaitAsync(TimeSpan.FromSeconds(8));

            await first.StopAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.True(tail.IsTailing("alpha"), "disconnecting one subscriber stopped the shared live tail");

            await File.AppendAllTextAsync(factory.AlphaLogPath, secondSentinel + Environment.NewLine);
            await secondSawSecond.Task.WaitAsync(TimeSpan.FromSeconds(8));

            await second.StopAsync();
            await WaitUntilAsync(() => !tail.IsTailing("alpha"), TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { await first.StopAsync(); } catch { }
            try { await second.StopAsync(); } catch { }
        }
    }

    [Fact]
    public async Task ReadOnly_log_tail_stop_does_not_stop_active_signalr_subscriber()
    {
        using var factory = new AgentTestFactory(alphaUsesLogProfile: true);
        var readOnlyToken = factory.CreateReadOnlyToken();
        await using var connection = factory.CreateHubConnection(readOnlyToken);
        using var client = factory.CreateClientWithToken(readOnlyToken);
        var tail = factory.Services.GetRequiredService<LogTailService>();
        var sentinel = "after-readonly-stop-" + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        connection.On<LogLine>("logLine", line =>
        {
            if (line.ServerId == "alpha" && line.Text == sentinel)
                received.TrySetResult(true);
        });

        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("SubscribeConsole", "alpha");
            await WaitUntilAsync(() => tail.IsTailing("alpha"), TimeSpan.FromSeconds(5));

            using var stopped = await client.PostAsJsonAsync("/api/servers/alpha/logs/tail", new { action = "stop" });
            Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
            Assert.True(tail.IsTailing("alpha"), "ReadOnly HTTP stop cancelled a subscriber-owned tail");

            await File.AppendAllTextAsync(factory.AlphaLogPath, sentinel + Environment.NewLine);
            await received.Task.WaitAsync(TimeSpan.FromSeconds(8));
        }
        finally
        {
            try { await connection.StopAsync(); } catch { }
        }
    }

    [Fact]
    public async Task Windows_runner_console_generation_change_replays_new_generation_from_sequence_one()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var factory = new AgentTestFactory(alphaUsesStdinProfile: true);
        factory.FakeRunner.ConsoleStateResponse = new RunnerResponse(
            true,
            InputAvailable: true,
            Console: new RunnerConsoleSnapshot(
                true,
                true,
                "old runner",
                3,
                new[]
                {
                    RunnerLine(1, "old-1"),
                    RunnerLine(2, "old-2"),
                    RunnerLine(3, "old-3")
                },
                "old-generation"));
        factory.FakeRunner.PollConsoleResponse = new RunnerResponse(
            true,
            InputAvailable: false,
            Console: new RunnerConsoleSnapshot(
                true,
                false,
                "new runner",
                5,
                new[]
                {
                    RunnerLine(1, "new-1"),
                    RunnerLine(2, "new-2"),
                    RunnerLine(3, "new-3"),
                    RunnerLine(4, "new-4"),
                    RunnerLine(5, "new-5")
                },
                "new-generation"));

        var received = new List<LogLine>();
        var gate = new object();
        await using var connection = factory.CreateHubConnection(AgentTestFactory.AdminToken);
        connection.On<LogLine>("logLine", line =>
        {
            lock (gate) received.Add(line);
        });

        await connection.StartAsync();
        try
        {
            await connection.InvokeAsync("SubscribeConsole", "alpha");
            await WaitUntilAsync(() =>
            {
                lock (gate) return received.Any(line => line.Text == "new-5");
            }, TimeSpan.FromSeconds(5));

            string[] newGenerationLines;
            lock (gate)
            {
                newGenerationLines = received
                    .Where(line => line.Text.StartsWith("new-", StringComparison.Ordinal))
                    .Select(line => line.Text)
                    .ToArray();
            }

            Assert.Equal(new[] { "new-1", "new-2", "new-3", "new-4", "new-5" }, newGenerationLines);
            Assert.Equal(3, factory.FakeRunner.LastPollAfterSequence);
        }
        finally
        {
            try { await connection.StopAsync(); } catch { }
        }
    }

    [Fact]
    public async Task Windows_runner_console_polling_stops_after_subscriber_disconnects()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var factory = new AgentTestFactory(alphaUsesStdinProfile: true);
        factory.FakeRunner.ConsoleStateResponse = new RunnerResponse(
            true,
            InputAvailable: true,
            Console: new RunnerConsoleSnapshot(
                true,
                true,
                "fake stdin console",
                0,
                Array.Empty<RunnerConsoleLine>()));

        await using var connection = factory.CreateHubConnection(AgentTestFactory.AdminToken);
        await connection.StartAsync();
        await connection.InvokeAsync("SubscribeConsole", "alpha");

        await WaitUntilAsync(() => factory.FakeRunner.PollConsoleCalls >= 2, TimeSpan.FromSeconds(5));
        await connection.StopAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        var afterDisconnect = factory.FakeRunner.PollConsoleCalls;
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        Assert.Equal(afterDisconnect, factory.FakeRunner.PollConsoleCalls);
    }

    private static RunnerConsoleLine RunnerLine(long sequence, string text) =>
        new(sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), "stdout", text);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for the integration-test condition.");
            await Task.Delay(25);
        }
    }
}
