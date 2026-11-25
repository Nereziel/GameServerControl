using GameServerControl.Agent.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class UpdateCheckerLifecycleTests
{
    private static UpdateChecker Create() =>
        new(new ConfigurationBuilder().Build(), NullLogger<UpdateChecker>.Instance);

    [Fact]
    public async Task Dispose_before_stop_is_safe_and_cancels_pending_poll()
    {
        using var checker = Create();
        await checker.StartAsync(CancellationToken.None);
        checker.Dispose();
        await checker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        checker.Dispose();
    }

    [Fact]
    public async Task Concurrent_shutdown_and_disposal_are_idempotent()
    {
        using var checker = Create();
        await checker.StartAsync(CancellationToken.None);
        await Task.WhenAll(
            Task.Run(() => checker.Dispose()),
            Task.Run(() => checker.StopAsync(CancellationToken.None)),
            Task.Run(() => checker.StopAsync(CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await checker.StopAsync(CancellationToken.None);
    }
}
