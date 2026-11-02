using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

public sealed record CreatedTokenRecord(
    string Id,
    string Name,
    string Token,
    TokenRole Role,
    DateTimeOffset CreatedAt);

internal sealed record StoredTokenRecord
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string TokenHash { get; init; } = "";
    public string TokenPrefix { get; init; } = "********";

    // Backward-compat migration input only. SaveLocked always clears it, so a managed
    // bearer token is never intentionally persisted in reusable plaintext form again.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Token { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TokenRole Role { get; init; } = TokenRole.Admin;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Multi-token store backed by tokens.json. Managed tokens are stored only as a SHA-256
/// hash plus a non-secret prefix. The legacy single Agent:ApiToken setting still works
/// and is treated as an Admin token for backward compatibility.
/// </summary>
public sealed class TokenRegistry
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly string _legacyToken;
    private readonly ILogger<TokenRegistry> _logger;
    private readonly object _gate = new();
    private List<StoredTokenRecord> _tokens = new();

    public TokenRegistry(IConfiguration cfg, ILogger<TokenRegistry> logger)
    {
        _logger = logger;
        _legacyToken = cfg["Agent:ApiToken"] ?? "";
        var dataDir = cfg["Agent:DataDir"] ?? Path.GetDirectoryName(typeof(TokenRegistry).Assembly.Location) ?? Environment.CurrentDirectory;
        _path = Path.Combine(dataDir, "tokens.json");
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _tokens = new();
                    return;
                }

                SecretFilePermissions.RestrictExisting(_path);
                var loaded = JsonSerializer.Deserialize<List<StoredTokenRecord>>(File.ReadAllText(_path), JsonOpts) ?? new();
                var migrated = false;
                var valid = new List<StoredTokenRecord>(loaded.Count);
                foreach (var item in loaded)
                {
                    if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name))
                    {
                        _logger.LogWarning("Ignoring invalid token record with missing id/name in {Path}", _path);
                        migrated = true;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(item.TokenHash))
                    {
                        if (!IsValidHash(item.TokenHash))
                        {
                            _logger.LogWarning("Ignoring invalid token hash for {Id}", item.Id);
                            migrated = true;
                            continue;
                        }

                        valid.Add(item with
                        {
                            TokenHash = item.TokenHash.ToLowerInvariant(),
                            TokenPrefix = string.IsNullOrWhiteSpace(item.TokenPrefix) ? "********" : item.TokenPrefix,
                            Token = null
                        });
                        if (item.Token is not null || item.TokenHash != item.TokenHash.ToLowerInvariant()) migrated = true;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(item.Token))
                    {
                        valid.Add(item with
                        {
                            TokenHash = HashToken(item.Token),
                            TokenPrefix = TokenPrefix(item.Token),
                            Token = null
                        });
                        migrated = true;
                        continue;
                    }

                    _logger.LogWarning("Ignoring token record {Id} because it contains no credential hash", item.Id);
                    migrated = true;
                }

                _tokens = valid;
                if (migrated)
                {
                    SaveLocked();
                    _logger.LogInformation("Migrated managed API token storage to hashed credentials in {Path}", _path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "tokens.json parse failed; starting with empty registry");
                _tokens = new();
            }
        }
    }

    /// <summary>
    /// Look up a presented bearer token. Returns (id, name, role) on success or null on miss.
    /// Falls back to the legacy single token in appsettings -> mapped to ("legacy", "Legacy", Admin).
    /// </summary>
    public (string Id, string Name, TokenRole Role)? Lookup(string presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        if (!string.IsNullOrEmpty(_legacyToken) && SecretEquals(presented, _legacyToken))
            return ("legacy", "Legacy (appsettings)", TokenRole.Admin);

        lock (_gate)
        {
            foreach (var token in _tokens)
                if (HashMatches(presented, token.TokenHash))
                    return (token.Id, token.Name, token.Role);
        }
        return null;
    }

    public IReadOnlyList<TokenMetadata> ListMetadata()
    {
        lock (_gate)
        {
            return _tokens
                .Select(t => new TokenMetadata(t.Id, t.Name, t.TokenPrefix, t.Role, t.CreatedAt))
                .ToArray();
        }
    }

    public CreatedTokenRecord Create(string id, string name, TokenRole role)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");

        var plaintext = GenerateToken();
        var now = DateTimeOffset.UtcNow;
        var stored = new StoredTokenRecord
        {
            Id = id.Trim(),
            Name = name.Trim(),
            TokenHash = HashToken(plaintext),
            TokenPrefix = TokenPrefix(plaintext),
            Token = null,
            Role = role,
            CreatedAt = now
        };

        lock (_gate)
        {
            if (_tokens.Any(t => string.Equals(t.Id, stored.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Token id already exists: " + stored.Id);
            _tokens.Add(stored);
            SaveLocked();
        }

        // Plaintext intentionally exists only in this one-time response and the caller's request context.
        return new CreatedTokenRecord(stored.Id, stored.Name, plaintext, stored.Role, stored.CreatedAt);
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var i = _tokens.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            _tokens.RemoveAt(i);
            SaveLocked();
            return true;
        }
    }

    private void SaveLocked()
    {
        SecretFilePermissions.WriteAllTextAtomic(_path, JsonSerializer.Serialize(_tokens, JsonOpts));
    }

    private static string TokenPrefix(string token) =>
        token.Length <= 8 ? "********" : token[..8] + "...";

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static bool IsValidHash(string hash)
    {
        if (hash.Length != 64) return false;
        try { return Convert.FromHexString(hash).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool HashMatches(string presented, string storedHash)
    {
        if (!IsValidHash(storedHash)) return false;
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = Convert.FromHexString(storedHash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }

    private static bool SecretEquals(string a, string b)
    {
        var ah = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var bh = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(ah, bh);
    }
}
