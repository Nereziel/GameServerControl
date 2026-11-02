using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Shared;
using Microsoft.Extensions.Configuration;

namespace GameServerControl.Agent.Security;

/// <summary>
/// Moves reusable Agent configuration credentials out of appsettings.json and into
/// purpose-bound encrypted state. Higher-precedence runtime values remain runtime-only.
/// </summary>
public sealed class ConfigurationSecretStore
{
    public const string Sentinel = "<stored-encrypted>";
    private const int CurrentVersion = 1;
    private static readonly HashSet<string> SensitiveLeafNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiToken", "CertPassword", "ApiKey", "Password"
    };
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "REPLACE_WITH_32_CHARS+", "CHANGE_ME", "your-token-here", "<generate>", Sentinel
    };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _appsettingsPath;
    private readonly string _statePath;
    private readonly SecretProtector _protector;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values;

    private sealed class StateDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record DiskSecret(string Path, JsonObject Parent, string Name, string Value);

    public ConfigurationSecretStore(string baseDirectory, SecretProtector protector)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Secret state base directory is required.", nameof(baseDirectory));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        var fullBase = Path.GetFullPath(baseDirectory);
        _appsettingsPath = Path.Combine(fullBase, "appsettings.json");
        _statePath = Path.Combine(fullBase, "secret-state.json");
        _values = LoadState();
    }

    public string StatePath => _statePath;

    public void MigrateAndApply(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            JsonObject? root = null;
            var appsettingsDirty = false;
            var stateDirty = false;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(_appsettingsPath))
            {
                SecretFilePermissions.RestrictExisting(_appsettingsPath);
                root = LoadAppSettings();
                if (root["Agent"] is JsonObject agent)
                {
                    foreach (var disk in EnumerateSensitiveLeaves(agent, "Agent"))
                    {
                        seenPaths.Add(disk.Path);
                        var runtimeBefore = configuration[disk.Path];
                        if (IsReusableSecret(disk.Value))
                        {
                            _values[disk.Path] = _protector.Protect(disk.Value, Purpose(disk.Path));
                            stateDirty = true;
                            disk.Parent[disk.Name] = Sentinel;
                            appsettingsDirty = true;
                            if (string.Equals(runtimeBefore, disk.Value, StringComparison.Ordinal) || IsPlaceholder(runtimeBefore))
                                configuration[disk.Path] = disk.Value;
                        }
                        else if (_values.ContainsKey(disk.Path) && !string.Equals(disk.Value, Sentinel, StringComparison.Ordinal))
                        {
                            disk.Parent[disk.Name] = Sentinel;
                            appsettingsDirty = true;
                        }

                        if (_values.TryGetValue(disk.Path, out var protectedValue))
                        {
                            if (IsPlaceholder(configuration[disk.Path]))
                                configuration[disk.Path] = _protector.Unprotect(protectedValue, Purpose(disk.Path));
                        }
                        else if (string.Equals(disk.Value, Sentinel, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"Encrypted configuration state is missing required secret '{disk.Path}'.");
                        }
                    }
                }
            }

            foreach (var (path, protectedValue) in _values)
            {
                if (seenPaths.Contains(path)) continue;
                if (IsPlaceholder(configuration[path]))
                    configuration[path] = _protector.Unprotect(protectedValue, Purpose(path));
            }

            if (stateDirty) SaveStateLocked();
            if (appsettingsDirty && root is not null) SaveSanitizedAppSettings(root);
        }
    }

    /// <summary>
    /// Resolves a secret needed before the normal startup migration point. A concrete
    /// higher-precedence runtime override is returned but never persisted. Plaintext
    /// physically present in appsettings is still migrated and scrubbed.
    /// </summary>
    public string ResolveEffectiveSecret(string path, string effectiveValue)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Configuration secret path is required.", nameof(path));
        lock (_gate)
        {
            var root = File.Exists(_appsettingsPath) ? LoadAppSettings() : null;
            var disk = root is null ? null : FindLeaf(root, path);
            var stateDirty = false;
            var appsettingsDirty = false;

            if (disk is not null && IsReusableSecret(disk.Value))
            {
                _values[path] = _protector.Protect(disk.Value, Purpose(path));
                stateDirty = true;
                disk.Parent[disk.Name] = Sentinel;
                appsettingsDirty = true;
            }
            else if (disk is not null && _values.ContainsKey(path) && !string.Equals(disk.Value, Sentinel, StringComparison.Ordinal))
            {
                disk.Parent[disk.Name] = Sentinel;
                appsettingsDirty = true;
            }

            if (stateDirty) SaveStateLocked();
            if (appsettingsDirty && root is not null) SaveSanitizedAppSettings(root);

            if (!IsPlaceholder(effectiveValue) && (disk is null || !string.Equals(effectiveValue, disk.Value, StringComparison.Ordinal)))
                return effectiveValue;
            if (_values.TryGetValue(path, out var protectedValue))
                return _protector.Unprotect(protectedValue, Purpose(path));
            return effectiveValue;
        }
    }

    public void SetSecret(IConfiguration configuration, string path, string value)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        SetSecretValue(path, value);
        configuration[path] = value;
    }

    public void SetSecretValue(string path, string value)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Configuration secret path is required.", nameof(path));
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Configuration secret value is required.", nameof(value));
        if (!path.StartsWith("Agent:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only Agent configuration secrets may be persisted.", nameof(path));
        lock (_gate)
        {
            _values[path] = _protector.Protect(value, Purpose(path));
            SaveStateLocked();
            ScrubAppSettingsPath(path);
        }
    }

    public string? GetSecret(string path)
    {
        lock (_gate)
            return _values.TryGetValue(path, out var protectedValue)
                ? _protector.Unprotect(protectedValue, Purpose(path))
                : null;
    }

    private Dictionary<string, string> LoadState()
    {
        if (!File.Exists(_statePath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SecretFilePermissions.RestrictExisting(_statePath);
        var doc = JsonSerializer.Deserialize<StateDocument>(File.ReadAllText(_statePath), JsonOptions)
            ?? throw new InvalidOperationException("secret-state.json is empty or invalid.");
        if (doc.Version != CurrentVersion)
            throw new InvalidOperationException($"Unsupported secret-state.json version {doc.Version}.");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, protectedValue) in doc.Values)
        {
            if (string.IsNullOrWhiteSpace(path) || !SecretProtector.IsProtected(protectedValue))
                throw new InvalidOperationException("secret-state.json contains an invalid unprotected entry.");
            _ = _protector.Unprotect(protectedValue, Purpose(path));
            values[path] = protectedValue;
        }
        return values;
    }

    private void SaveStateLocked()
    {
        var doc = new StateDocument { Version = CurrentVersion, Values = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase) };
        SecretFilePermissions.WriteAllTextAtomic(_statePath, JsonSerializer.Serialize(doc, JsonOptions));
    }

    private JsonObject LoadAppSettings()
        => JsonNode.Parse(File.ReadAllText(_appsettingsPath)) as JsonObject
           ?? throw new InvalidOperationException("appsettings.json must contain a JSON object.");

    private void SaveSanitizedAppSettings(JsonObject root)
    {
        var sanitized = root.ToJsonString(JsonOptions);
        SecretFilePermissions.WriteAllTextAtomic(_appsettingsPath, sanitized);
        SecretFilePermissions.WriteAllTextAtomic(_appsettingsPath + ".bak", sanitized);
    }

    private void ScrubAppSettingsPath(string path)
    {
        if (!File.Exists(_appsettingsPath)) return;
        var root = LoadAppSettings();
        var segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return;
        JsonObject current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[segments[i]] = child;
            }
            current = child;
        }
        current[segments[^1]] = Sentinel;
        SaveSanitizedAppSettings(root);
    }

    private static DiskSecret? FindLeaf(JsonObject root, string path)
    {
        var segments = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        JsonObject current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject child) return null;
            current = child;
        }
        if (current[segments[^1]] is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        return new DiskSecret(path, current, segments[^1], text ?? "");
    }

    private static IEnumerable<DiskSecret> EnumerateSensitiveLeaves(JsonObject obj, string prefix)
    {
        foreach (var pair in obj.ToArray())
        {
            var path = prefix + ":" + pair.Key;
            if (pair.Value is JsonObject child)
            {
                foreach (var nested in EnumerateSensitiveLeaves(child, path)) yield return nested;
                continue;
            }
            if (!SensitiveLeafNames.Contains(pair.Key) || pair.Value is not JsonValue value) continue;
            if (!value.TryGetValue<string>(out var text)) continue;
            yield return new DiskSecret(path, obj, pair.Key, text ?? "");
        }
    }

    private static bool IsReusableSecret(string? value) => !IsPlaceholder(value) && !SecretProtector.IsProtected(value);
    private static bool IsPlaceholder(string? value) => string.IsNullOrWhiteSpace(value) || PlaceholderValues.Contains(value.Trim());
    private static string Purpose(string path) => "config:" + path;
}
