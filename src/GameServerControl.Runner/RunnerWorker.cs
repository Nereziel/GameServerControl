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
        if (_manifest.AutoStart)
        {
            var result = await _processHost.StartAsync(stoppingToken);
            if (!result.Ok)
                _logger.LogError("Runner autostart failed: {Error}", result.Error);
        }

        await _pipeServer.RunAsync(stoppingToken);
    }
}
