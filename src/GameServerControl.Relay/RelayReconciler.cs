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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
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
