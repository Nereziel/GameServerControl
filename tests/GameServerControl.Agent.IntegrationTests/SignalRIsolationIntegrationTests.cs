using System.Net;
using System.Net.Http.Json;
using GameServerControl.Shared;
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
}
