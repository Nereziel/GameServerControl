using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Networking;

public sealed class PublicRelayManager
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ServerRegistry _registry;
    private readonly ILogger<PublicRelayManager> _logger;
    private string _baseUrl;
    private string _apiToken;
    private string? _configuredTarget;
    private string? _publicAddress;
    private readonly string _statePath;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _lastApplied = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PublicRelayServerSettings> _settings;

    private sealed record StoredGlobalSettings(
        string Url,
        string ApiToken,
        string? TargetAddress,
        string? PublicAddress);

    private sealed record GlobalSnapshot(
        string Url,
        string ApiToken,
        string? TargetAddress,
        string? PublicAddress)
    {
        public bool Configured => !string.IsNullOrWhiteSpace(Url) && ApiToken.Length >= 24;
    }

    private sealed class StateDocument
    {
        public StoredGlobalSettings? Global { get; set; }
        public Dictionary<string, PublicRelayServerSettings>? Servers { get; set; }
    }

    public PublicRelayManager(IConfiguration cfg, IHostEnvironment env, ServerRegistry registry, ILogger<PublicRelayManager> logger)
    {
        _registry = registry;
        _logger = logger;
        var configuredState = cfg["Agent:PublicRelay:StatePath"] ?? "public-relay.json";
        _statePath = Path.IsPathRooted(configuredState) ? configuredState : Path.Combine(env.ContentRootPath, configuredState);

        var state = LoadState();
        _settings = new Dictionary<string, PublicRelayServerSettings>(state.Servers ?? new(), StringComparer.OrdinalIgnoreCase);
        if (state.Global is not null)
        {
            _baseUrl = state.Global.Url.TrimEnd('/');
            _apiToken = state.Global.ApiToken;
            _configuredTarget = NullIfBlank(state.Global.TargetAddress);
            _publicAddress = NullIfBlank(state.Global.PublicAddress);
        }
        else
        {
            _baseUrl = (cfg["Agent:PublicRelay:Url"] ?? "").TrimEnd('/');
            _apiToken = cfg["Agent:PublicRelay:ApiToken"] ?? "";
            _configuredTarget = NullIfBlank(cfg["Agent:PublicRelay:TargetAddress"]);
            _publicAddress = NullIfBlank(cfg["Agent:PublicRelay:PublicAddress"]);
        }
    }

    public bool Configured => SnapshotGlobal().Configured;

    public PublicRelayServerSettings SettingsFor(string serverId)
    {
        lock (_stateGate)
            return _settings.TryGetValue(serverId, out var value)
                ? new PublicRelayServerSettings(value.Enabled, value.PublicPorts)
                : new PublicRelayServerSettings(false, null);
    }

    public async Task<PublicRelayServerStatus> GetStatusAsync(string serverId, CancellationToken ct = default)
    {
        var def = _registry.Get(serverId) ?? throw new KeyNotFoundException(serverId);
        var settings = SettingsFor(serverId);
        var global = SnapshotGlobal();
        string? target = null;
        RelayRoutePort[] ports = Array.Empty<RelayRoutePort>();
        string? error = null;

        try
        {
            target = ResolveTargetAddress(global.TargetAddress);
            ports = BuildRoute(def, settings, target).Ports;
        }
        catch (Exception ex) { error = ex.Message; }

        var reachable = false;
        if (global.Configured)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, "/api/health", null, global, ct);
                reachable = response.IsSuccessStatusCode;
                if (!reachable && error is null) error = await ResponseErrorAsync(response, ct);
            }
            catch (Exception ex) { if (error is null) error = ex.Message; }
        }
        else if (error is null)
        {
            error = "Public relay is not configured on this Agent.";
        }

        return new PublicRelayServerStatus(
            global.Configured,
            settings.Enabled,
            reachable,
            global.PublicAddress,
            target,
            ports,
            error,
            global.Url,
            !string.IsNullOrWhiteSpace(global.ApiToken),
            global.TargetAddress);
    }

    public async Task<PublicRelayServerStatus> SaveAsync(string serverId, PublicRelayServerSettings settings, bool serverRunning, CancellationToken ct = default)
    {
        var def = _registry.Get(serverId) ?? throw new KeyNotFoundException(serverId);
        await _mutationGate.WaitAsync(ct);
        try
        {
            var currentGlobal = SnapshotGlobal();
            var candidateGlobal = CandidateGlobal(currentGlobal, settings);
            ValidateGlobal(candidateGlobal);

            var serverSettings = new PublicRelayServerSettings(settings.Enabled, settings.PublicPorts);
            var target = ResolveTargetAddress(candidateGlobal.TargetAddress);
            _ = BuildRoute(def, serverSettings, target);
            ValidatePublicConflicts(serverId, serverSettings, target);

            if (serverSettings.Enabled && !candidateGlobal.Configured)
                throw new InvalidOperationException("Relay is enabled for this server but the Agent relay URL/token are not configured.");

            var connectionChanged = !string.Equals(currentGlobal.Url, candidateGlobal.Url, StringComparison.OrdinalIgnoreCase) ||
                                    !string.Equals(currentGlobal.ApiToken, candidateGlobal.ApiToken, StringComparison.Ordinal);
            if (connectionChanged && currentGlobal.Configured)
                await RemoveRoutesFromOldRelayBestEffortAsync(currentGlobal, ct);

            lock (_stateGate)
            {
                _baseUrl = candidateGlobal.Url;
                _apiToken = candidateGlobal.ApiToken;
                _configuredTarget = candidateGlobal.TargetAddress;
                _publicAddress = candidateGlobal.PublicAddress;
                _settings[serverId] = serverSettings;
                SaveStateLocked();
            }
            _lastApplied.Clear();

            await EnsureForServerAsync(def, serverRunning, ct);
            return await GetStatusAsync(serverId, ct);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task RemoveServerAsync(string serverId, CancellationToken ct = default)
    {
        await _mutationGate.WaitAsync(ct);
        try
        {
            lock (_stateGate)
            {
                if (_settings.Remove(serverId)) SaveStateLocked();
            }
            _lastApplied.TryRemove(serverId, out _);
            var global = SnapshotGlobal();
            if (!global.Configured) return;
            try { using var _ = await SendAsync(HttpMethod.Delete, $"/api/routes/{Uri.EscapeDataString(serverId)}", null, global, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not remove public relay route for {ServerId}", serverId); }
        }
        finally { _mutationGate.Release(); }
    }

    public async Task EnsureForServerAsync(ServerDef def, bool running, CancellationToken ct = default)
    {
        var settings = SettingsFor(def.Id);
        var global = SnapshotGlobal();
        if (!settings.Enabled || !running)
        {
            const string desired = "off";
            if (_lastApplied.TryGetValue(def.Id, out var previous) && previous == desired) return;
            if (global.Configured)
            {
                using var response = await SendAsync(HttpMethod.Delete, $"/api/routes/{Uri.EscapeDataString(def.Id)}", null, global, ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(await ResponseErrorAsync(response, ct));
            }
            _lastApplied[def.Id] = desired;
            return;
        }

        if (!global.Configured)
            throw new InvalidOperationException("Public relay is enabled but the Agent relay URL/token are not configured.");

        var target = ResolveTargetAddress(global.TargetAddress);
        var route = BuildRoute(def, settings, target);
        var desiredHash = JsonSerializer.Serialize(route, JsonOptions);
        if (_lastApplied.TryGetValue(def.Id, out var last) && last == desiredHash) return;

        using var put = await SendAsync(HttpMethod.Put, $"/api/routes/{Uri.EscapeDataString(def.Id)}", route, global, ct);
        if (!put.IsSuccessStatusCode)
            throw new InvalidOperationException(await ResponseErrorAsync(put, ct));
        _lastApplied[def.Id] = desiredHash;
    }

    private RelayRouteDefinition BuildRoute(ServerDef def, PublicRelayServerSettings settings, string targetAddress)
    {
        var targetPorts = def.NetworkPorts ?? GameProfiles.FindFor(def)?.DefaultPorts ?? Array.Empty<NetworkPort>();
        var overrides = settings.PublicPorts ?? Array.Empty<RelayPublicPort>();
        var routes = new List<RelayRoutePort>();

        foreach (var target in targetPorts)
        {
            var publicPort = overrides.FirstOrDefault(p => p.Protocol == target.Protocol &&
                p.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
            var publicStart = publicPort?.Port ?? target.Port;
            var publicEnd = publicPort?.EndPort ?? target.EndPort;
            ValidateRange(publicStart, publicEnd, "public", target.Name);
            ValidateRange(target.Port, target.EndPort, "target", target.Name);
            if ((publicEnd ?? publicStart) - publicStart != (target.EndPort ?? target.Port) - target.Port)
                throw new ArgumentException($"Public port range '{target.Name}' must contain the same number of ports as its target range.");

            routes.Add(new RelayRoutePort(target.Name, target.Protocol, publicStart, publicEnd, target.Port, target.EndPort));
        }

        foreach (var extra in overrides)
        {
            if (!targetPorts.Any(p => p.Protocol == extra.Protocol && p.Name.Equals(extra.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Public relay override '{extra.Name}' does not match a current server network port.");
        }

        return new RelayRouteDefinition(def.Id, targetAddress, routes.ToArray());
    }

    private void ValidatePublicConflicts(string candidateId, PublicRelayServerSettings candidate, string targetAddress)
    {
        if (!candidate.Enabled) return;
        var definitions = new List<RelayRouteDefinition>();
        foreach (var def in _registry.All)
        {
            var serverSettings = string.Equals(def.Id, candidateId, StringComparison.OrdinalIgnoreCase) ? candidate : SettingsFor(def.Id);
            if (!serverSettings.Enabled) continue;
            definitions.Add(BuildRoute(def, serverSettings, targetAddress));
        }

        var all = definitions.SelectMany(d => d.Ports.Select(p => (d.ServerId, Port: p))).ToArray();
        for (var i = 0; i < all.Length; i++)
        for (var j = i + 1; j < all.Length; j++)
        {
            var a = all[i]; var b = all[j];
            if (a.Port.Protocol != b.Port.Protocol || !Overlap(a.Port.PublicPort, a.Port.PublicEndPort, b.Port.PublicPort, b.Port.PublicEndPort)) continue;
            throw new ArgumentException($"Public relay port conflict: {a.ServerId}/{a.Port.Name} overlaps {b.ServerId}/{b.Port.Name}.");
        }
    }

    private GlobalSnapshot CandidateGlobal(GlobalSnapshot current, PublicRelayServerSettings settings)
    {
        var url = settings.RelayUrl is null ? current.Url : settings.RelayUrl.Trim().TrimEnd('/');
        var token = settings.RelayApiToken is null ? current.ApiToken : settings.RelayApiToken.Trim();
        var target = settings.TargetAddress is null ? current.TargetAddress : NullIfBlank(settings.TargetAddress);
        var publicAddress = settings.PublicAddress is null ? current.PublicAddress : NullIfBlank(settings.PublicAddress);
        return new GlobalSnapshot(url, token, target, publicAddress);
    }

    private static void ValidateGlobal(GlobalSnapshot global)
    {
        if (!string.IsNullOrWhiteSpace(global.Url))
        {
            if (!Uri.TryCreate(global.Url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Relay URL must be an absolute http:// or https:// URL, normally the VPS Tailscale IP and port 5100.");
        }
        if (!string.IsNullOrWhiteSpace(global.ApiToken) && global.ApiToken.Length < 24)
            throw new ArgumentException("Relay API token must contain at least 24 characters.");
    }

    private async Task RemoveRoutesFromOldRelayBestEffortAsync(GlobalSnapshot oldGlobal, CancellationToken ct)
    {
        foreach (var def in _registry.All)
        {
            if (!SettingsFor(def.Id).Enabled) continue;
            try
            {
                using var response = await SendAsync(HttpMethod.Delete, $"/api/routes/{Uri.EscapeDataString(def.Id)}", null, oldGlobal, ct);
                if (!response.IsSuccessStatusCode)
                    _logger.LogWarning("Old relay returned HTTP {Status} while removing {ServerId}", (int)response.StatusCode, def.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove {ServerId} from the previous public relay connection", def.Id);
            }
        }
    }

    private string ResolveTargetAddress(string? configuredTarget)
    {
        if (configuredTarget is not null) return configuredTarget;

        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (Interface: n, Address: a.Address)))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork && IsTailscaleAddress(x.Address))
            .OrderByDescending(x => x.Interface.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
                                    x.Interface.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
            throw new InvalidOperationException("Could not detect this Agent's Tailscale IPv4 address. Set the relay TargetAddress explicitly.");
        return candidates[0].Address.ToString();
    }

    private static bool IsTailscaleAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b.Length == 4 && b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    private GlobalSnapshot SnapshotGlobal()
    {
        lock (_stateGate)
            return new GlobalSnapshot(_baseUrl, _apiToken, _configuredTarget, _publicAddress);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, GlobalSnapshot global, CancellationToken ct)
    {
        if (!global.Configured) throw new InvalidOperationException("Public relay is not configured.");
        var request = new HttpRequestMessage(method, global.Url + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", global.ApiToken);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        return await Http.SendAsync(request, ct);
    }

    private static async Task<string> ResponseErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? $"Relay returned HTTP {(int)response.StatusCode}." : text;
    }

    private StateDocument LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new StateDocument();
            return JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(_statePath), JsonOptions) ?? new StateDocument();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not load public relay state from {Path}", _statePath);
            return new StateDocument();
        }
    }

    private void SaveStateLocked()
    {
        var dir = Path.GetDirectoryName(_statePath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        var global = new StoredGlobalSettings(_baseUrl, _apiToken, _configuredTarget, _publicAddress);
        var json = JsonSerializer.Serialize(new StateDocument { Global = global, Servers = _settings }, JsonOptions);
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _statePath, overwrite: true);
    }

    private static void ValidateRange(int start, int? endRaw, string kind, string name)
    {
        var end = endRaw ?? start;
        if (start is < 1 or > 65535 || end is < 1 or > 65535 || end < start)
            throw new ArgumentException($"Invalid {kind} relay range for {name}: {start}-{end}.");
    }

    private static bool Overlap(int a, int? aeRaw, int b, int? beRaw)
    {
        var ae = aeRaw ?? a; var be = beRaw ?? b;
        return a <= be && b <= ae;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
