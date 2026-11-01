using System.Text;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Reads and writes RuneScape: Dragonwilds DedicatedServer.ini plus the two official launch ports.
/// Dragonwilds rewrites DedicatedServer.ini while running, so writes are rejected until the
/// server is stopped. Unknown INI keys (ServerGuid/future game settings/etc.) are preserved.
/// </summary>
public sealed class DragonwildsConfig : IGameConfig
{
    private const string Section = "[/Script/Dominion.DedicatedServerSettings]";

    private readonly GuestProcessService _guest;
    private readonly LocalProcessService _local;
    private readonly ServerStore _store;
    private readonly IConfiguration _cfg;
    private readonly ILogger<DragonwildsConfig> _logger;

    public DragonwildsConfig(
        GuestProcessService guest,
        LocalProcessService local,
        ServerStore store,
        IConfiguration cfg,
        ILogger<DragonwildsConfig> logger)
    {
        _guest = guest;
        _local = local;
        _store = store;
        _cfg = cfg;
        _logger = logger;
    }

    private GuestCredential GetCred(string? id)
    {
        var key = string.IsNullOrEmpty(id) ? "default" : id;
        var s = _cfg.GetSection($"Agent:GuestCredentials:{key}");
        return new GuestCredential { Username = s["Username"] ?? "", Password = s["Password"] ?? "" };
    }

    private static string[] IniPathCandidates(ServerDef def)
    {
        var root = Path.Combine(def.GuestWorkingDir, "RSDragonwilds", "Saved", "Config");
        if (def.HostingMode == HostingMode.Vm || OperatingSystem.IsWindows())
            return new[] { Path.Combine(root, "WindowsServer", "DedicatedServer.ini") };

        // Jagex's how-to guide names Linux; their current container changed to LinuxServer.
        return new[]
        {
            Path.Combine(root, "LinuxServer", "DedicatedServer.ini"),
            Path.Combine(root, "Linux", "DedicatedServer.ini")
        };
    }

    private static string LocalIniPath(ServerDef def)
    {
        var candidates = IniPathCandidates(def);
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public async Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        string content;
        if (def.HostingMode == HostingMode.BareMetal)
        {
            var path = LocalIniPath(def);
            content = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";
        }
        else
        {
            content = await ReadViaPsDirectAsync(def, IniPathCandidates(def)[0], ct) ?? "";
        }

        var values = Parse(content);
        values["Port"] = ReadLaunchPort(def, "Port", 7777).ToString();
        var gamePort = int.Parse(values["Port"]);
        values["BeaconPort"] = ReadLaunchPort(def, "BeaconPort", Math.Min(65535, gamePort + 1111)).ToString();
        values["Administrators"] = ParseAdministratorList(values.TryGetValue("AdministratorList", out var admins) ? admins : "");
        return values;
    }

    public async Task<string?> ValidateForStartAsync(ServerDef def, CancellationToken ct)
    {
        if (def.HostingMode != HostingMode.BareMetal)
            return "RuneScape: Dragonwilds is supported only in BareMetal hosting mode.";

        var path = LocalIniPath(def);
        if (!File.Exists(path))
            return "Dragonwilds is not configured yet. Open Configuration and set Owner Player ID, Server name, Default world name and Admin password before the first start.";

        var values = Parse(await File.ReadAllTextAsync(path, ct));
        var missing = new List<string>();
        if (!HasValue(values, "OwnerId")) missing.Add("Owner Player ID");
        if (!HasValue(values, "ServerName")) missing.Add("Server name");
        if (!HasValue(values, "DefaultWorldName")) missing.Add("Default world name");
        if (!HasValue(values, "AdminPassword")) missing.Add("Admin password");
        if (missing.Count > 0)
            return "Dragonwilds configuration is incomplete: " + string.Join(", ", missing) + ". Open Configuration and save the required values before starting the server.";

        var port = ReadLaunchPort(def, "Port", 7777);
        var beaconPort = ReadLaunchPort(def, "BeaconPort", port <= 64424 ? port + 1111 : 8888);
        if (port is < 1 or > 65535 || beaconPort is < 1 or > 65535 || port == beaconPort)
            return "Dragonwilds network configuration is invalid. Set distinct Game port and Beacon port values in Configuration.";

        return null;
    }

    public async Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        await EnsureStoppedAsync(def, ct);

        var path = def.HostingMode == HostingMode.BareMetal ? LocalIniPath(def) : IniPathCandidates(def)[0];
        var current = def.HostingMode == HostingMode.BareMetal
            ? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "")
            : await ReadViaPsDirectAsync(def, path, ct) ?? "";

        var merged = Parse(current);
        foreach (var (key, value) in values)
        {
            if (key is "Port" or "BeaconPort" or "Administrators") continue;
            merged[key] = value ?? "";
        }
        if (values.TryGetValue("Administrators", out var administrators))
            merged["AdministratorList"] = FormatAdministratorList(administrators);

        Require(merged, "OwnerId", "Owner Player ID is required by the current Jagex dedicated-server guide.");
        Require(merged, "ServerName", "Server name is required.");
        Require(merged, "DefaultWorldName", "Default world name is required.");
        Require(merged, "AdminPassword", "Admin password is required.");

        var portRaw = values.TryGetValue("Port", out var configuredPort)
            ? configuredPort : ReadLaunchPort(def, "Port", 7777).ToString();
        if (!int.TryParse(portRaw, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Dragonwilds game port must be between 1 and 65535.");

        var defaultBeacon = port <= 64424 ? port + 1111 : 8888;
        var beaconRaw = values.TryGetValue("BeaconPort", out var configuredBeacon)
            ? configuredBeacon : ReadLaunchPort(def, "BeaconPort", defaultBeacon).ToString();
        if (!int.TryParse(beaconRaw, out var beaconPort) || beaconPort is < 1 or > 65535)
            throw new ArgumentException("Dragonwilds beacon port must be between 1 and 65535.");
        if (port == beaconPort)
            throw new ArgumentException("Dragonwilds game and beacon ports must be different.");

        var lines = current.Replace("\r\n", "\n").Split('\n').ToList();
        if (!lines.Any(l => l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase)))
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add(Section);
        }

        foreach (var key in new[]
        {
            "OwnerId", "ServerName", "DefaultWorldName", "AdminPassword", "WorldPassword", "AdministratorList"
        })
        {
            if (!merged.TryGetValue(key, out var value)) continue;
            SetKey(lines, key, value ?? "");
        }

        var output = string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n";
        var updatedDef = def with
        {
            StartArgs = WriteLaunchPorts(def.StartArgs ?? Array.Empty<string>(), port, beaconPort),
            NetworkPorts = new[]
            {
                new NetworkPort("Game", NetworkProtocol.Udp, port),
                new NetworkPort("Beacon", NetworkProtocol.Udp, beaconPort)
            }
        };

        // Validate/persist both reservations before touching the game config. Roll back the
        // ServerDef if the file write itself fails so config/network metadata cannot drift.
        _store.Update(def.Id, updatedDef);
        var writeOk = def.HostingMode == HostingMode.BareMetal
            ? await WriteLocalAsync(path, output, ct)
            : await WriteViaPsDirectAsync(def, path, output, ct);

        if (!writeOk)
        {
            try { _store.Update(def.Id, def); }
            catch (Exception rollbackEx) { _logger.LogWarning(rollbackEx, "Dragonwilds ServerDef rollback failed for {Id}", def.Id); }
        }
        return writeOk;
    }

    private async Task EnsureStoppedAsync(ServerDef def, CancellationToken ct)
    {
        if (def.HostingMode == HostingMode.BareMetal)
        {
            var (ok, pid) = await _local.FindProcessAsync(def.GuestExePath, ct);
            if (ok && pid is not null)
                throw new InvalidOperationException("Stop the Dragonwilds server before changing DedicatedServer.ini; the game overwrites live edits.");
            return;
        }

        var cred = GetCred(def.GuestCredentialId);
        var (guestOk, guestPid) = await _guest.FindProcessAsync(def.VmName, cred, def.GuestExePath, ct);
        if (guestOk && guestPid is not null)
            throw new InvalidOperationException("Stop the Dragonwilds server before changing DedicatedServer.ini; the game overwrites live edits.");
    }

    private async Task<bool> WriteLocalAsync(string path, string output, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, output, new UTF8Encoding(false), ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local write to {Path} failed", path);
            return false;
        }
    }

    private static void Require(Dictionary<string, string> values, string key, string message)
    {
        if (!HasValue(values, key))
            throw new ArgumentException(message);
    }

    private static bool HasValue(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    private static int ReadLaunchPort(ServerDef def, string switchName, int fallback)
    {
        var args = def.StartArgs ?? Array.Empty<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            var prefix = "-" + switchName + "=";
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(arg[prefix.Length..], out var inline))
                return inline;
            if (arg.Equals("-" + switchName, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var separate))
                return separate;
        }

        var named = def.NetworkPorts?.FirstOrDefault(p => p.Protocol == NetworkProtocol.Udp && p.Name.Equals(switchName == "Port" ? "Game" : "Beacon", StringComparison.OrdinalIgnoreCase));
        return named?.Port ?? fallback;
    }

    private static string[] WriteLaunchPorts(string[] existing, int port, int beaconPort)
    {
        var result = new List<string>();
        for (var i = 0; i < existing.Length; i++)
        {
            var arg = existing[i];
            var isPort = arg.StartsWith("-Port=", StringComparison.OrdinalIgnoreCase) || arg.Equals("-Port", StringComparison.OrdinalIgnoreCase);
            var isBeacon = arg.StartsWith("-BeaconPort=", StringComparison.OrdinalIgnoreCase) || arg.Equals("-BeaconPort", StringComparison.OrdinalIgnoreCase);
            if (isPort || isBeacon)
            {
                if (!arg.Contains('=') && i + 1 < existing.Length && !existing[i + 1].StartsWith("-", StringComparison.Ordinal)) i++;
                continue;
            }
            result.Add(arg);
        }
        result.Add($"-Port={port}");
        result.Add($"-BeaconPort={beaconPort}");
        return result.ToArray();
    }

    private static string ParseAdministratorList(string value)
    {
        var raw = (value ?? "").Trim();
        if (raw.StartsWith('(') && raw.EndsWith(')') && raw.Length >= 2) raw = raw[1..^1];
        return string.Join("\n", raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string FormatAdministratorList(string value)
    {
        var ids = (value ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return "(" + string.Join(',', ids) + ")";
    }

    private static Dictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("["))
            {
                inSection = line.Equals(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return result;
    }

    private static void SetKey(List<string> lines, string key, string value)
    {
        var sectionIndex = lines.FindIndex(l => l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0) return;

        var end = lines.Count;
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[")) { end = i; break; }
        }

        for (var i = sectionIndex + 1; i < end; i++)
        {
            var line = lines[i].TrimStart();
            if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
            lines[i] = key + "=" + value;
            return;
        }
        lines.Insert(end, key + "=" + value);
    }

    private async Task<string?> ReadViaPsDirectAsync(ServerDef def, string path, CancellationToken ct)
    {
        var cred = GetCred(def.GuestCredentialId);
        var safePath = path.Replace("'", "''");
        var cmd = $"powershell -NoProfile -Command \"if (Test-Path -LiteralPath '{safePath}') {{ Get-Content -LiteralPath '{safePath}' -Raw }} else {{ '' }}\"";
        var (ok, output) = await _guest.RunCommandInGuestAsync(def.VmName, cred, cmd, def.GuestWorkingDir, TimeSpan.FromSeconds(20), ct);
        return ok ? output : null;
    }

    private async Task<bool> WriteViaPsDirectAsync(ServerDef def, string path, string content, CancellationToken ct)
    {
        var cred = GetCred(def.GuestCredentialId);
        var bytes = Encoding.UTF8.GetBytes(content);
        var b64 = Convert.ToBase64String(bytes);
        var dir = Path.GetDirectoryName(path)!.Replace("'", "''");
        var safePath = path.Replace("'", "''");
        var script = "powershell -NoProfile -Command \"" +
            $"New-Item -ItemType Directory -Force -Path '{dir}' | Out-Null; " +
            $"[IO.File]::WriteAllBytes('{safePath}', [Convert]::FromBase64String('{b64}'))" +
            "\"";
        var (ok, output) = await _guest.RunCommandInGuestAsync(def.VmName, cred, script, def.GuestWorkingDir, TimeSpan.FromSeconds(30), ct);
        if (!ok) _logger.LogError("PS-Direct write to {Path} failed: {Out}", path, output);
        return ok;
    }
}
