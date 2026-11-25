namespace GameServerControl.Relay;

public sealed class RelayReconciler : BackgroundService
{
    private readonly RelayStateStore _store;
    private readonly IptablesRelay _iptables;
    private readonly ILogger<RelayReconciler> _logger;

    public RelayReconciler(RelayStateStore store, IptablesRelay iptables, ILogger<RelayReconciler> logger)
    {
        _store = store;
        _iptables = iptables;
        _logger = logger;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the reconciliation loop before removing the firewall chains.
        await base.StopAsync(cancellationToken);
        try
        {
            await _store.MutationGate.WaitAsync(cancellationToken);
            try
            {
                await _iptables.RebuildAsync(Array.Empty<GameServerControl.Shared.RelayRouteDefinition>(), cancellationToken);
            }
            finally { _store.MutationGate.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not flush public relay rules during shutdown");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.MutationGate.WaitAsync(stoppingToken);
                try
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var routes = _store.Snapshot();
                    if (first)
                    {
                        await _iptables.RebuildAsync(routes, stoppingToken);
                        first = false;
                    }
                    else
                    {
                        await _iptables.EnsureAsync(routes, stoppingToken);
                    }
                }
                finally { _store.MutationGate.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Relay firewall reconciliation failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
