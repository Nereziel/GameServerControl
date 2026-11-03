using GameServerControl.Shared;

namespace GameServerControl.Runner;

public sealed class RunnerWorker : BackgroundService
{
    private readonly RunnerManifest _manifest;
    private readonly RunnerProcessHost _processHost;
    private readonly RunnerPipeServer _pipeServer;
    private readonly ILogger<RunnerWorker> _logger;

    public RunnerWorker(
        RunnerManifest manifest,
        RunnerProcessHost processHost,
        RunnerPipeServer pipeServer,
        ILogger<RunnerWorker> logger)
    {
        _manifest = manifest;
        _processHost = processHost;
        _pipeServer = pipeServer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Windows starts automatic Runner services on machine boot. Retry a transient
        // startup failure, then leave the service alive for the control pipe.
        if (_manifest.AutoStart)
        {
            for (var attempt = 1; attempt <= 3 && !stoppingToken.IsCancellationRequested; attempt++)
            {
                var result = await _processHost.StartAsync(stoppingToken);
                if (result.Ok) break;
                _logger.LogWarning("Runner autostart attempt {Attempt}/3 failed: {Error}", attempt, result.Error);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var monitor = MonitorProcessAsync(monitorStop.Token);
        try { await _pipeServer.RunAsync(stoppingToken); }
        finally
        {
            await monitorStop.CancelAsync();
            await monitor;
        }
    }

    private async Task MonitorProcessAsync(CancellationToken ct)
    {
        var attempts = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
                if (!_processHost.RestartDesired)
                {
                    attempts = 0;
                    continue;
                }

                var state = await _processHost.FindAsync(ct);
                if (!state.Ok)
                {
                    _logger.LogWarning("Crash recovery deferred: process status is unknown: {Error}", state.Error);
                    continue;
                }
                if (state.Pid is not null) continue;
                if (attempts >= 3)
                {
                    _logger.LogError("Crash recovery exhausted three attempts. Start the server manually to retry.");
                    // Avoid repeated errors. Explicit StartAsync resets recovery demand.
                    break;
                }

                attempts++;
                _logger.LogWarning("Unexpected server exit detected; recovery attempt {Attempt}/3.", attempts);
                await Task.Delay(TimeSpan.FromSeconds(attempts * 10), ct);
                if (!_processHost.RestartDesired) continue;
                var restart = await _processHost.StartAsync(ct);
                if (!restart.Ok)
                    _logger.LogError("Crash recovery attempt {Attempt} failed: {Error}", attempts, restart.Error);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
