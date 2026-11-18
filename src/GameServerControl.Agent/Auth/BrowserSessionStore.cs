using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Opt-in loopback browser sessions: the cookie is a random opaque credential,
/// only its hash is persisted, and API requests still use short-lived bearer
/// credentials. Sessions are bound to the originating Admin token identity.
/// </summary>
public sealed class BrowserSessionStore
{
    public const string CookieName = "gsc_local_browser";
    public static readonly TimeSpan RememberDuration = TimeSpan.FromDays(30);
    public static readonly TimeSpan AccessDuration = TimeSpan.FromHours(8);
    private const int MaxSessions = 20;

    private sealed record Remembered(string Hash, string TokenId, string CredentialHash, DateTimeOffset ExpiresAt);
    private sealed record Access(string TokenId, string CredentialHash, string ParentHash, DateTimeOffset ExpiresAt);

    private readonly object _gate = new();
    private readonly Dictionary<string, Remembered> _remembered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Access> _access = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ILogger<BrowserSessionStore> _logger;

    public BrowserSessionStore(IConfiguration config, IHostEnvironment environment, ILogger<BrowserSessionStore> logger)
    {
        var dir = config["Agent:DataDir"] ?? environment.ContentRootPath;
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(environment.ContentRootPath, dir);
        _path = Path.Combine(dir, "remembered-browsers.json");
        _logger = logger;
        try
        {
            if (!File.Exists(_path)) return;
            SecretFilePermissions.RestrictExisting(_path);
            if (new FileInfo(_path).Length > 128 * 1024) return;
            var items = JsonSerializer.Deserialize<Remembered[]>(File.ReadAllText(_path)) ?? [];
            foreach (var item in items.Where(x => x.ExpiresAt > DateTimeOffset.UtcNow &&
                x.Hash.Length == 64 && x.CredentialHash.Length == 64 && x.TokenId.Length <= 256).Take(MaxSessions))
                _remembered[item.Hash] = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not load remembered browser sessions ({Reason})", ex.GetType().Name);
        }
    }

    private static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _remembered.Where(e => e.Value.ExpiresAt <= now)
                     .Select(e => e.Key).ToArray()) _remembered.Remove(key);
        foreach (var key in _access.Where(e => e.Value.ExpiresAt <= now)
                     .Select(e => e.Key).ToArray()) _access.Remove(key);
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        SecretFilePermissions.WriteAllTextAtomic(_path,
            JsonSerializer.Serialize(_remembered.Values.ToArray()));
    }

    public string Remember(string tokenId, string originalCredential, TokenRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || tokenId.Length > 256)
            throw new ArgumentException("Invalid authenticated token identity.", nameof(tokenId));
        var existing = registry.Lookup(originalCredential);
        if (existing is null || existing.Value.Id != tokenId || existing.Value.Role != TokenRole.Admin)
            throw new UnauthorizedAccessException("Remembered sessions require a current Admin API token.");
        var cookie = Generate();
        var hash = Hash(cookie);
        var credentialHash = Hash(originalCredential).ToLowerInvariant();
        lock (_gate)
        {
            Prune(DateTimeOffset.UtcNow);
            while (_remembered.Count >= MaxSessions)
            {
                var oldest = _remembered.Values.MinBy(x => x.ExpiresAt)!;
                _remembered.Remove(oldest.Hash);
            }
            _remembered[hash] = new Remembered(hash, tokenId, credentialHash, DateTimeOffset.UtcNow.Add(RememberDuration));
            try { Save(); }
            catch
            {
                _remembered.Remove(hash);
                throw;
            }
        }
        return cookie;
    }

    public string? Restore(string? cookie, TokenRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Length != 43) return null;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            Prune(now);
            if (!_remembered.TryGetValue(Hash(cookie), out var entry)) return null;
            if (!registry.IsAdminCredentialHashActive(entry.TokenId, entry.CredentialHash))
            {
                _remembered.Remove(entry.Hash);
                Save();
                return null;
            }
            var bearer = Generate();
            _access[Hash(bearer)] = new Access(entry.TokenId, entry.CredentialHash, entry.Hash, now.Add(AccessDuration));
            return bearer;
        }
    }

    public (string Id, string Name, TokenRole Role)? Lookup(string? bearer, TokenRegistry registry)
    {
        if (string.IsNullOrEmpty(bearer) || bearer.Length != 43) return null;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            Prune(now);
            if (!_access.TryGetValue(Hash(bearer), out var entry)) return null;
            if (!registry.IsAdminCredentialHashActive(entry.TokenId, entry.CredentialHash)) return null;
            return (entry.TokenId, "Remembered local browser", TokenRole.Admin);
        }
    }

    public void Forget(string? cookie)
    {
        if (string.IsNullOrWhiteSpace(cookie) || cookie.Length != 43) return;
        lock (_gate)
        {
            var hash = Hash(cookie);
            foreach (var key in _access.Where(e => e.Value.ParentHash == hash).Select(e => e.Key).ToArray())
                _access.Remove(key);
            if (_remembered.Remove(hash)) Save();
        }
    }
}

/// <summary>Only direct same-origin loopback requests can create or restore sessions.</summary>
public static class BrowserSessionAccess
{
    public static bool Allowed(HttpContext context)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        var host = context.Request.Host.Host;
        if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) &&
            host != "127.0.0.1" && host != "[::1]" && host != "::1") return false;

        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Length > 0 && fetchSite is not ("same-origin" or "none")) return false;
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                !string.Equals(originUri.Host, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(originUri.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) ||
                originUri.Port != context.Request.Host.Port.GetValueOrDefault(
                    context.Request.IsHttps ? 443 : 80)) return false;
        }
        return true;
    }

    public static CookieOptions CookieOptions(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = "/auth",
        MaxAge = BrowserSessionStore.RememberDuration,
        IsEssential = true
    };
}
