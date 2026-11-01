using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Valheim configures via launch args on valheim_server. ServerDef.StartArgs is the source of truth.
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

    private readonly ServerStore _store;
    public ValheimConfig(ServerStore store) { _store = store; }

    public Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var args = def.StartArgs ?? Array.Empty<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("-", StringComparison.Ordinal)) continue;
            var bare = a.TrimStart('-');

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

            // Read legacy GameServerControl args too, then normalize them to -setkey on save.
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
                dict[bare] = args[i + 1];
                i++;
            }
        }

        return Task.FromResult(dict);
    }

    public Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
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

        _store.Update(def.Id, updated);
        return Task.FromResult(true);
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
            {
                preserved.Add(existing[++i]);
            }
        }
        return preserved;
    }

    private static bool IsTrue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
}
