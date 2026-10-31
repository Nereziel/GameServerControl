using System.Text;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Reads and writes RuneScape: Dragonwilds DedicatedServer.ini.
/// Dragonwilds rewrites this file while running, so callers should stop the server
/// before changing settings. We preserve unknown keys such as ServerGuid/admin state.
/// </summary>
public sealed class DragonwildsConfig : IGameConfig
{
    private const string Section = "[/Script/Dominion.DedicatedServerSettings]";

    private readonly GuestProcessService _guest;
    private readonly IConfiguration _cfg;
    private readonly ILogger<DragonwildsConfig> _logger;

    public DragonwildsConfig(GuestProcessService guest, IConfiguration cfg, ILogger<DragonwildsConfig> logger)
    {
        _guest = guest;
        _cfg = cfg;
        _logger = logger;
    }

    private GuestCredential GetCred(string? id)
    {
        var key = string.IsNullOrEmpty(id) ? "default" : id;
        var s = _cfg.GetSection($"Agent:GuestCredentials:{key}");
        return new GuestCredential { Username = s["Username"] ?? "", Password = s["Password"] ?? "" };
    }

    private static string IniPath(ServerDef def)
    {
        // PowerShell Direct/Hyper-V guests are Windows. Bare-metal agents use the
        // platform-specific path documented by Jagex: WindowsServer on Windows, Linux on Linux.
        var platformDir = def.HostingMode == HostingMode.Vm || OperatingSystem.IsWindows()
            ? "WindowsServer"
            : "Linux";
        return Path.Combine(def.GuestWorkingDir, "RSDragonwilds", "Saved", "Config", platformDir, "DedicatedServer.ini");
    }

    public async Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var path = IniPath(def);
        var content = def.HostingMode == HostingMode.BareMetal
            ? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "")
            : await ReadViaPsDirectAsync(def, path, ct) ?? "";

        return Parse(content);
    }

    public async Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        var path = IniPath(def);
        var current = def.HostingMode == HostingMode.BareMetal
            ? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "")
            : await ReadViaPsDirectAsync(def, path, ct) ?? "";

        var lines = current.Replace("\r\n", "\n").Split('\n').ToList();
        if (!lines.Any(l => l.Trim().Equals(Section, StringComparison.OrdinalIgnoreCase)))
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add(Section);
        }

        foreach (var key in new[] { "OwnerId", "ServerName", "DefaultWorldName", "AdminPassword", "WorldPassword" })
        {
            if (!values.TryGetValue(key, out var value)) continue;
            SetKey(lines, key, value ?? "");
        }

        var output = string.Join("\r\n", lines).TrimEnd('\r', '\n') + "\r\n";

        if (def.HostingMode == HostingMode.BareMetal)
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

        return await WriteViaPsDirectAsync(def, path, output, ct);
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
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            result[key] = value;
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
            if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = key + "=" + value;
                return;
            }
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
