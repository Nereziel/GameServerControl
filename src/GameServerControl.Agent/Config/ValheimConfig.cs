using System.Text;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Valheim configures primarily via launch args on valheim_server. ServerDef.StartArgs is the
/// source of truth for launch settings; the three official permission files live in -savedir.
/// Managed fields follow Iron Gate's dedicated-server syntax, including -setkey for checkbox-style
/// world modifiers. Unknown launch args are preserved so manual Unity/BepInEx flags are not lost.
/// </summary>
public sealed class ValheimConfig : IGameConfig
{
    private static readonly HashSet<string> BoolFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "crossplay"
    };

    private static readonly HashSet<string> SetKeyFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nobuildcost", "playerevents", "passivemobs", "nomap"
    };

    private static readonly HashSet<string> KeyValueArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "password", "port", "world", "savedir", "saveinterval", "backups",
        "backupshort", "backuplong", "preset", "public", "instanceid"
    };

    private static readonly (string Key, string FileName)[] AccessFiles =
    {
        ("adminlist", "adminlist.txt"),
        ("bannedlist", "bannedlist.txt"),
        ("permittedlist", "permittedlist.txt")
    };

    private readonly ServerStore _store;
    private readonly GuestProcessService _guest;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ValheimConfig> _logger;

    public ValheimConfig(
        ServerStore store,
        GuestProcessService guest,
        IConfiguration cfg,
        ILogger<ValheimConfig> logger)
    {
        _store = store;
        _guest = guest;
        _cfg = cfg;
        _logger = logger;
    }

    public async Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var dict = ReadLaunchArgs(def.StartArgs ?? Array.Empty<string>());
        var saveDir = ResolveSaveDir(def, dict);

        foreach (var (key, fileName) in AccessFiles)
        {
            var text = await ReadAccessFileAsync(def, Path.Combine(saveDir, fileName), ct);
            dict[key] = NormalizeAccessList(text);
        }

        return dict;
    }

    public async Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        var preserved = PreserveUnknownArgs(def.StartArgs ?? Array.Empty<string>());
        var managed = new List<string>();

        foreach (var key in new[]
        {
            "name", "password", "port", "world", "savedir", "saveinterval", "backups",
            "backupshort", "backuplong", "public", "instanceid", "preset"
        })
        {
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            managed.Add("-" + key);
            managed.Add(value.Trim());
        }

        foreach (var key in new[] { "combat", "deathpenalty", "resources", "raids", "portals" })
        {
            if (!values.TryGetValue("modifier:" + key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
            managed.Add("-modifier");
            managed.Add(key);
            managed.Add(value.Trim());
        }

        foreach (var flag in SetKeyFlags.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!values.TryGetValue(flag, out var value) || !IsTrue(value)) continue;
            managed.Add("-setkey");
            managed.Add(flag);
        }

        if (values.TryGetValue("crossplay", out var crossplay) && IsTrue(crossplay))
            managed.Add("-crossplay");

        var args = preserved.Concat(managed).ToArray();
        var updated = def with { StartArgs = args };

        if (values.TryGetValue("port", out var portRaw) && int.TryParse(portRaw, out var port))
        {
            if (port is < 1 or > 65534)
                throw new ArgumentException("Valheim port must be between 1 and 65534 because the server also uses port+1.");
            updated = updated with
            {
                NetworkPorts = new[] { new NetworkPort("Game", NetworkProtocol.Udp, port, port + 1) }
            };
        }

        var saveDir = values.TryGetValue("savedir", out var saveDirRaw) && !string.IsNullOrWhiteSpace(saveDirRaw)
            ? saveDirRaw.Trim()
            : ResolveSaveDir(def, ReadLaunchArgs(args));
        updated = updated with { SaveDirs = new[] { saveDir } };

        // Persist/validate the definition first so a port collision cannot leave permission files
        // changed after the API rejects the configuration. Roll back the definition on file failure.
        _store.Update(def.Id, updated);
        try
        {
            foreach (var (key, fileName) in AccessFiles)
            {
                if (!values.TryGetValue(key, out var text)) continue;
                if (!await WriteAccessFileAsync(def, Path.Combine(saveDir, fileName), NormalizeAccessList(text), ct))
                    throw new IOException($"Could not write {fileName}.");
            }
            return true;
        }
        catch
        {
            try { _store.Update(def.Id, def); }
            catch (Exception rollbackEx) { _logger.LogWarning(rollbackEx, "Valheim ServerDef rollback failed for {Id}", def.Id); }
            throw;
        }
    }

    private static Dictionary<string, string> ReadLaunchArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal)) continue;
            var bare = arg.TrimStart('-');

            if (bare.Equals("modifier", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
            {
                dict[$"modifier:{args[i + 1]}"] = args[i + 2];
                i += 2;
                continue;
            }

            if (bare.Equals("setkey", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var key = args[i + 1];
                if (SetKeyFlags.Contains(key)) dict[key] = "true";
                i++;
                continue;
            }

            // Read legacy GameServerControl direct flags too, then normalize them to -setkey on save.
            if (SetKeyFlags.Contains(bare))
            {
                dict[bare] = "true";
                continue;
            }

            if (BoolFlags.Contains(bare))
            {
                dict[bare] = "true";
                continue;
            }

            if (KeyValueArgs.Contains(bare) && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                dict[bare] = args[++i];
            }
        }
        return dict;
    }

    private string ResolveSaveDir(ServerDef def, IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("savedir", out var explicitDir) && !string.IsNullOrWhiteSpace(explicitDir))
            return explicitDir.Trim();
        if (def.SaveDirs is { Length: > 0 } && !string.IsNullOrWhiteSpace(def.SaveDirs[0]))
            return def.SaveDirs[0];

        if (def.HostingMode == HostingMode.Vm)
            return Path.Combine(def.GuestWorkingDir, "data");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsWindows()
            ? Path.Combine(home, "AppData", "LocalLow", "IronGate", "Valheim")
            : Path.Combine(home, ".config", "unity3d", "IronGate", "Valheim");
    }

    private GuestCredential GetCred(string? id)
    {
        var key = string.IsNullOrWhiteSpace(id) ? "default" : id;
        var section = _cfg.GetSection($"Agent:GuestCredentials:{key}");
        return new GuestCredential
        {
            Username = section["Username"] ?? "",
            Password = section["Password"] ?? ""
        };
    }

    private async Task<string> ReadAccessFileAsync(ServerDef def, string path, CancellationToken ct)
    {
        if (def.HostingMode == HostingMode.BareMetal)
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "";

        var safePath = path.Replace("'", "''");
        var cmd = $"powershell -NoProfile -Command \"if (Test-Path -LiteralPath '{safePath}') {{ Get-Content -LiteralPath '{safePath}' -Raw }} else {{ '' }}\"";
        var (ok, output) = await _guest.RunCommandInGuestAsync(def.VmName, GetCred(def.GuestCredentialId), cmd,
            def.GuestWorkingDir, TimeSpan.FromSeconds(20), ct);
        return ok ? output : "";
    }

    private async Task<bool> WriteAccessFileAsync(ServerDef def, string path, string text, CancellationToken ct)
    {
        var output = string.IsNullOrWhiteSpace(text) ? "" : text.TrimEnd() + Environment.NewLine;
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
                _logger.LogError(ex, "Valheim access-list write to {Path} failed", path);
                return false;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(output.Replace(Environment.NewLine, "\r\n"));
        var b64 = Convert.ToBase64String(bytes);
        var dir = Path.GetDirectoryName(path)!.Replace("'", "''");
        var safePath = path.Replace("'", "''");
        var script = "powershell -NoProfile -Command \"" +
            $"New-Item -ItemType Directory -Force -Path '{dir}' | Out-Null; " +
            $"[IO.File]::WriteAllBytes('{safePath}', [Convert]::FromBase64String('{b64}'))" +
            "\"";
        var (ok, commandOutput) = await _guest.RunCommandInGuestAsync(def.VmName, GetCred(def.GuestCredentialId), script,
            def.GuestWorkingDir, TimeSpan.FromSeconds(30), ct);
        if (!ok) _logger.LogError("Valheim access-list write to {Path} failed: {Out}", path, commandOutput);
        return ok;
    }

    private static string NormalizeAccessList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return string.Join("\n", text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));
    }

    private static List<string> PreserveUnknownArgs(string[] existing)
    {
        var preserved = new List<string>();
        for (var i = 0; i < existing.Length; i++)
        {
            var arg = existing[i];
            if (!arg.StartsWith("-", StringComparison.Ordinal))
            {
                preserved.Add(arg);
                continue;
            }

            var bare = arg.TrimStart('-');
            if (bare.Equals("modifier", StringComparison.OrdinalIgnoreCase))
            {
                i = Math.Min(existing.Length - 1, i + 2);
                continue;
            }
            if (bare.Equals("setkey", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < existing.Length) i++;
                continue;
            }
            if (SetKeyFlags.Contains(bare) || BoolFlags.Contains(bare))
                continue;
            if (KeyValueArgs.Contains(bare))
            {
                if (i + 1 < existing.Length && !existing[i + 1].StartsWith("-", StringComparison.Ordinal)) i++;
                continue;
            }

            preserved.Add(arg);
            if (i + 1 < existing.Length && !existing[i + 1].StartsWith("-", StringComparison.Ordinal))
                preserved.Add(existing[++i]);
        }
        return preserved;
    }

    private static bool IsTrue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
