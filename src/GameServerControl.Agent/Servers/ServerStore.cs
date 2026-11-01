using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Networking;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed class ServerStore
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly ServerRegistry _registry;
    private readonly ILogger<ServerStore> _logger;
    private readonly object _lock = new();

    public ServerStore(IConfiguration cfg, IHostEnvironment env, ServerRegistry registry, ILogger<ServerStore> logger)
    {
        _registry = registry;
        _logger = logger;
        var configured = cfg["Agent:ServersJsonPath"] ?? "servers.json";
        _path = Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    public ServerDef Add(ServerDef def)
    {
        var normalized = Normalize(def);
        Validate(normalized);
        lock (_lock)
        {
            var list = LoadAll();
            if (list.Any(s => string.Equals(s.Id, normalized.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Server with id '{normalized.Id}' already exists.");
            ValidatePortConflicts(normalized, list, excludeId: null);
            list.Add(normalized);
            SaveAll(list);
            _registry.Reload();
        }
        return normalized;
    }

    public ServerDef Update(string id, ServerDef def)
    {
        var normalized = Normalize(def with { Id = id });
        Validate(normalized);
        lock (_lock)
        {
            var list = LoadAll();
            var idx = list.FindIndex(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new KeyNotFoundException(id);
            ValidatePortConflicts(normalized, list, id);
            list[idx] = normalized;
            SaveAll(list);
            _registry.Reload();
        }
        return normalized;
    }

    public bool Delete(string id)
    {
        ServerDef? removedDef = null;
        lock (_lock)
        {
            var list = LoadAll();
            removedDef = list.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
            if (removedDef is null) return false;
            list.Remove(removedDef);
            SaveAll(list);
            _registry.Reload();
        }

        if (!WindowsFirewall.RemoveProgramRules(id, out var firewallError))
            _logger.LogWarning("Could not remove Windows Firewall rules for {Id}: {Error}", id, firewallError);
        return true;
    }

    private static ServerDef Normalize(ServerDef def)
    {
        def = CuratedNetwork.Normalize(def);
        if (def.NetworkPorts is not null) return def;
        var defaults = GameProfiles.FindFor(def)?.DefaultPorts;
        return def with { NetworkPorts = defaults?.ToArray() ?? Array.Empty<NetworkPort>() };
    }

    private static void Validate(ServerDef def)
    {
        if (string.IsNullOrWhiteSpace(def.Id) || !IdPattern.IsMatch(def.Id))
            throw new ArgumentException("Id must be lowercase letters, digits, and hyphens (e.g. 'valheim-2').");
        if (string.IsNullOrWhiteSpace(def.Name))
            throw new ArgumentException("Name is required.");

        var profile = GameProfiles.FindFor(def);
        if (def.HostingMode == HostingMode.Vm && profile is not null && !profile.Capabilities.HyperV)
            throw new ArgumentException($"{profile.Label} is supported only in BareMetal hosting mode.");
        if (def.HostingMode == HostingMode.Vm && string.IsNullOrWhiteSpace(def.VmName))
            throw new ArgumentException("VmName is required when HostingMode is Vm.");
        if (string.IsNullOrWhiteSpace(def.GuestExePath))
            throw new ArgumentException("GuestExePath is required.");

        var ports = EffectivePorts(def);
        for (var i = 0; i < ports.Length; i++)
        {
            ValidatePort(ports[i]);
            if (def.HostingMode == HostingMode.BareMetal &&
                profile?.Key is "valheim" or "dragonwilds" && ports[i].Port < 1024)
            {
                throw new ArgumentException(
                    $"{profile.Label} ports must be 1024 or higher. The Linux Agent runs as an unprivileged service user and cannot bind privileged ports by default.");
            }
            for (var j = i + 1; j < ports.Length; j++)
            {
                if (ports[i].Protocol == ports[j].Protocol && Overlaps(ports[i], ports[j]))
                    throw new ArgumentException($"Network settings contain overlapping {ports[i].Protocol.ToString().ToUpperInvariant()} ports: {Describe(ports[i])} and {Describe(ports[j])}.");
            }
        }
    }

    private static void ValidatePortConflicts(ServerDef candidate, IEnumerable<ServerDef> all, string? excludeId)
    {
        if (candidate.HostingMode != HostingMode.BareMetal) return;
        var candidatePorts = EffectivePorts(candidate);
        if (candidatePorts.Length == 0) return;

        foreach (var other in all)
        {
            if (!string.IsNullOrWhiteSpace(excludeId) && string.Equals(other.Id, excludeId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (other.HostingMode != HostingMode.BareMetal) continue;

            foreach (var ours in candidatePorts)
            foreach (var theirs in EffectivePorts(other))
            {
                if (ours.Protocol != theirs.Protocol || !Overlaps(ours, theirs)) continue;
                throw new ArgumentException(
                    $"Port conflict with server '{other.Name}': {Describe(ours)} overlaps {Describe(theirs)}. " +
                    "Change one server's Network ports before running both on this machine.");
            }
        }
    }

    private static NetworkPort[] EffectivePorts(ServerDef def)
        => def.NetworkPorts ?? GameProfiles.FindFor(def)?.DefaultPorts ?? Array.Empty<NetworkPort>();

    private static void ValidatePort(NetworkPort p)
    {
        var end = p.EndPort ?? p.Port;
        if (p.Port is < 1 or > 65535 || end is < 1 or > 65535)
            throw new ArgumentException($"Invalid network port range '{Describe(p)}'. Ports must be between 1 and 65535.");
        if (end < p.Port)
            throw new ArgumentException($"Invalid network port range '{Describe(p)}'. End port must be greater than or equal to the start port.");
    }

    private static bool Overlaps(NetworkPort a, NetworkPort b)
    {
        var aEnd = a.EndPort ?? a.Port;
        var bEnd = b.EndPort ?? b.Port;
        return a.Port <= bEnd && b.Port <= aEnd;
    }

    private static string Describe(NetworkPort p)
    {
        var end = p.EndPort ?? p.Port;
        var range = end == p.Port ? p.Port.ToString() : $"{p.Port}-{end}";
        return $"{p.Protocol.ToString().ToUpperInvariant()} {range} ({p.Name})";
    }

    private List<ServerDef> LoadAll()
    {
        if (!File.Exists(_path)) return new List<ServerDef>();
        var json = File.ReadAllText(_path);
        var doc = JsonSerializer.Deserialize<ServersFile>(json, JsonOpts);
        return (doc?.Servers ?? Array.Empty<ServerDef>()).Select(Normalize).ToList();
    }

    private void SaveAll(List<ServerDef> list)
    {
        var doc = new ServersFile { Servers = list.ToArray() };
        var json = JsonSerializer.Serialize(doc, JsonOpts);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(_path))
            File.Replace(tmp, _path, _path + ".bak");
        else
            File.Move(tmp, _path);
    }

    private sealed class ServersFile
    {
        public ServerDef[]? Servers { get; set; }
    }
}
