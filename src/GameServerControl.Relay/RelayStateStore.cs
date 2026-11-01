using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Relay;

public sealed class RelayStateStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private RelayRouteDefinition[] _routes;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public RelayStateStore(IConfiguration configuration, IHostEnvironment environment, ILogger<RelayStateStore> logger)
    {
        var configured = configuration["Relay:StatePath"];
        _path = string.IsNullOrWhiteSpace(configured)
            ? (OperatingSystem.IsLinux()
                ? "/var/lib/gameservercontrol-relay/routes.json"
                : Path.Combine(environment.ContentRootPath, "relay-routes.json"))
            : (Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));

        try
        {
            if (!File.Exists(_path))
            {
                _routes = Array.Empty<RelayRouteDefinition>();
                return;
            }

            var json = File.ReadAllText(_path);
            _routes = JsonSerializer.Deserialize<RelayRouteDefinition[]>(json, JsonOptions) ?? Array.Empty<RelayRouteDefinition>();
            logger.LogInformation("Loaded {Count} relay route sets from {Path}", _routes.Length, _path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load relay state from {Path}", _path);
            _routes = Array.Empty<RelayRouteDefinition>();
        }
    }

    public RelayRouteDefinition[] Snapshot()
    {
        lock (_gate) return _routes.ToArray();
    }

    public RelayRouteDefinition? Get(string serverId)
    {
        lock (_gate)
            return _routes.FirstOrDefault(r => string.Equals(r.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
    }

    public void Replace(RelayRouteDefinition[] routes)
    {
        var ordered = routes.OrderBy(r => r.ServerId, StringComparer.OrdinalIgnoreCase).ToArray();
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(ordered, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);

        lock (_gate) _routes = ordered;
    }
}
