using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Security;

public static class AppSettingsSecretProtection
{
    private static readonly string[][] FixedSecretPaths =
    {
        new[] { "Agent", "ApiToken" },
        new[] { "Agent", "CertPassword" },
        new[] { "Agent", "PublicRelay", "ApiToken" },
        new[] { "Agent", "CurseForge", "ApiKey" }
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static void ProtectAndLoad(IConfiguration configuration, string appsettingsPath, SecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(protector);
        if (!File.Exists(appsettingsPath)) return;

        ProtectFile(appsettingsPath, configuration, protector);
        var backup = appsettingsPath + ".bak";
        if (File.Exists(backup)) ProtectFile(backup, null, protector);
    }

    private static void ProtectFile(string path, IConfiguration? runtimeConfiguration, SecretProtector protector)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions)?.AsObject()
            ?? throw new InvalidDataException($"Expected a JSON object in {path}.");
        var changed = false;

        foreach (var segments in FixedSecretPaths)
            changed |= ProtectPath(root, segments, runtimeConfiguration, protector);

        if (TryGetObject(root, new[] { "Agent", "GuestCredentials" }, out var credentials))
        {
            foreach (var credential in credentials.ToArray())
            {
                if (credential.Value is not JsonObject credentialObject) continue;
                changed |= ProtectProperty(
                    credentialObject,
                    "Password",
                    $"Agent:GuestCredentials:{credential.Key}:Password",
                    runtimeConfiguration,
                    protector);
            }
        }

        if (changed)
            SecretFilePermissions.WriteAllTextAtomic(path, root.ToJsonString(WriteOptions));
        else
            SecretFilePermissions.RestrictExisting(path);
    }

    private static bool ProtectPath(
        JsonObject root,
        IReadOnlyList<string> segments,
        IConfiguration? runtimeConfiguration,
        SecretProtector protector)
    {
        if (segments.Count < 2) return false;
        var parentSegments = segments.Take(segments.Count - 1).ToArray();
        if (!TryGetObject(root, parentSegments, out var parent)) return false;
        var key = segments[^1];
        return ProtectProperty(parent, key, string.Join(':', segments), runtimeConfiguration, protector);
    }

    private static bool ProtectProperty(
        JsonObject parent,
        string propertyName,
        string configPath,
        IConfiguration? runtimeConfiguration,
        SecretProtector protector)
    {
        var actualName = parent.Select(x => x.Key)
            .FirstOrDefault(k => string.Equals(k, propertyName, StringComparison.OrdinalIgnoreCase));
        if (actualName is null || parent[actualName] is not JsonValue value || !value.TryGetValue<string>(out var stored)) return false;
        if (string.IsNullOrEmpty(stored)) return false;

        if (protector.IsProtected(stored))
        {
            var plaintext = protector.Unprotect(stored);
            if (runtimeConfiguration is not null && string.Equals(runtimeConfiguration[configPath], stored, StringComparison.Ordinal))
                runtimeConfiguration[configPath] = plaintext;
            return false;
        }

        parent[actualName] = protector.Protect(stored);
        return true;
    }

    private static bool TryGetObject(JsonObject root, IReadOnlyList<string> segments, out JsonObject result)
    {
        JsonObject current = root;
        foreach (var segment in segments)
        {
            var actualName = current.Select(x => x.Key)
                .FirstOrDefault(k => string.Equals(k, segment, StringComparison.OrdinalIgnoreCase));
            if (actualName is null || current[actualName] is not JsonObject next)
            {
                result = null!;
                return false;
            }
            current = next;
        }
        result = current;
        return true;
    }
}
