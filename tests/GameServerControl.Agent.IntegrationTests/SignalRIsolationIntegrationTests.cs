using System.Net;
using System.Net.Http.Json;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR.Client;
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
