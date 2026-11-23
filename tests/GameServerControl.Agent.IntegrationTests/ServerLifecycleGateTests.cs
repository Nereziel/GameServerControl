using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameServerControl.Agent.Servers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ServerActionResult = GameServerControl.Shared.ActionResult;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ServerLifecycleGateTests
{
    [Fact]
    public async Task Restore_endpoint_rejects_busy_server_without_blocking_another_server()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var gate = factory.Services.GetRequiredService<ServerLifecycleGate>();

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeStart = gate.RunExclusiveAsync("alpha", async () =>
        {
            entered.SetResult(true);
            await release.Task;
            return Success();
        });

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await admin.PostAsync("/api/servers/alpha/backups/example.zip/restore", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
            Assert.Contains("another", payload.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Restore", payload.RootElement.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

            // Independent server operations must not be serialized globally.
            var otherServer = await gate.RunExclusiveAsync("beta", () => Task.FromResult(Success()));
            Assert.True(otherServer.Success);
        }
        finally
        {
            release.TrySetResult(true);
            Assert.True((await activeStart.WaitAsync(TimeSpan.FromSeconds(10))).Success);
        }
    }

    [Fact]
    public async Task Start_stop_restart_update_and_apply_cannot_overlap_a_restore()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var orchestrator = factory.Services.GetRequiredService<ServerOrchestrator>();
        var gate = factory.Services.GetRequiredService<ServerLifecycleGate>();

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The same gate that encloses RestoreCoreAsync is held to simulate an
        // in-progress restore without modifying real game save directories.
        var restore = gate.RunExclusiveAsync("alpha", async () =>
        {
            entered.SetResult(true);
            await release.Task;
            return Success();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var actions = new Func<Task<ServerActionResult>>[]
            {
                () => orchestrator.StartAsync("alpha"),
                () => orchestrator.StopAsync("alpha", stopVm: false, force: false),
                () => orchestrator.RestartAsync("alpha"),
                () => orchestrator.UpdateAsync("alpha"),
                () => orchestrator.ApplyConfigAsync("alpha")
            };

            foreach (var run in actions)
            {
                var result = await run().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(result.Success);
                Assert.Contains("Restore", result.Message, StringComparison.OrdinalIgnoreCase);
            }

            using var httpResponse = await admin.PostAsync("/api/servers/alpha/restart", null);
            Assert.Equal(HttpStatusCode.OK, httpResponse.StatusCode);
            var httpResult = await httpResponse.Content.ReadFromJsonAsync<ServerActionResult>();
            Assert.NotNull(httpResult);
            Assert.False(httpResult.Success);
            Assert.Contains("Restore", httpResult.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.TrySetResult(true);
            Assert.True((await restore.WaitAsync(TimeSpan.FromSeconds(10))).Success);
        }
    }

    [Fact]
    public async Task Gate_is_released_after_failure_and_supports_cancelled_waiter()
    {
        var gate = new ServerLifecycleGate();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunExclusiveAsync("alpha", () =>
                Task.FromException<ServerActionResult>(new InvalidOperationException("test failure"))));

        var next = await gate.RunExclusiveAsync("alpha", () => Task.FromResult(Success()));
        Assert.True(next.Success);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunExclusiveAsync("alpha", () => Task.FromResult(Success()), cancelled.Token));

        var afterCancellation = await gate.RunExclusiveAsync("alpha", () => Task.FromResult(Success()));
        Assert.True(afterCancellation.Success);
    }

    [Fact]
    public async Task Gate_is_case_insensitive_for_server_ids()
    {
        var gate = new ServerLifecycleGate();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = gate.RunExclusiveAsync("alpha", async () =>
        {
            entered.SetResult(true);
            await release.Task;
            return Success();
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var result = await gate.RunExclusiveAsync("ALPHA", () => Task.FromResult(Success()));
            Assert.False(result.Success);
        }
        finally
        {
            release.TrySetResult(true);
            Assert.True((await active.WaitAsync(TimeSpan.FromSeconds(10))).Success);
        }
    }

    private static ServerActionResult Success() => new(true, "ok", "test");
}
