using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

/// <summary>
/// Opt-in Windows loopback browser sign-in. The browser persists a non-exportable
/// ECDSA private key in origin-scoped IndexedDB. The Agent retains only a public
/// key and the hash of the original Admin API credential, never bearer secrets.
/// </summary>
public sealed class BrowserSessionStore
{
    public static readonly TimeSpan RememberDuration = TimeSpan.FromDays(30);
    public static readonly TimeSpan AccessDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan ChallengeDuration = TimeSpan.FromSeconds(30);
    private const int MaxSessions = 20;
    private const int MaxAccessTokens = 128;

    private sealed record Remembered(string Id, string TokenId, string CredentialHash,
        string PublicKey, DateTimeOffset ExpiresAt);
    private sealed record ChallengeState(string Value, DateTimeOffset ExpiresAt);
    private sealed record Access(string TokenId, string CredentialHash, string ParentId,
        DateTimeOffset ExpiresAt);

    private readonly object _gate = new();
    private readonly Dictionary<string, Remembered> _remembered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChallengeState> _challenges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Access> _access = new(StringComparer.Ordinal);
    private readonly string _path;
    private readonly ILogger<BrowserSessionStore> _logger;

    public BrowserSessionStore(IConfiguration configuration, IHostEnvironment environment,
        ILogger<BrowserSessionStore> logger)
    {
        var dir = configuration["Agent:DataDir"] ?? environment.ContentRootPath;
        if (!Path.IsPathRooted(dir)) dir = Path.Combine(environment.ContentRootPath, dir);
        _path = Path.Combine(dir, "remembered-browsers.json");
        _logger = logger;
        try
        {
            if (!File.Exists(_path)) return;
            SecretFilePermissions.RestrictExisting(_path);
            if (new FileInfo(_path).Length > 128 * 1024) return;
            var saved = JsonSerializer.Deserialize<Remembered[]>(File.ReadAllText(_path)) ?? [];
            foreach (var item in saved.Where(x => x.ExpiresAt > DateTimeOffset.UtcNow &&
                x.Id.Length == 43 && x.CredentialHash.Length == 64 &&
                x.TokenId.Length is > 0 and <= 256 &&
                x.PublicKey.Length is > 0 and <= 1024).Take(MaxSessions))
                _remembered[item.Id] = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("Could not load remembered browsers ({Reason})", ex.GetType().Name);
        }
    }

    private static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private void Prune(DateTimeOffset now)
    {
        foreach (var id in _remembered.Where(x => x.Value.ExpiresAt <= now)
                     .Select(x => x.Key).ToArray()) _remembered.Remove(id);
        foreach (var id in _challenges.Where(x => x.Value.ExpiresAt <= now)
                     .Select(x => x.Key).ToArray()) _challenges.Remove(id);
        foreach (var key in _access.Where(x => x.Value.ExpiresAt <= now)
                     .Select(x => x.Key).ToArray()) _access.Remove(key);
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        SecretFilePermissions.WriteAllTextAtomic(_path,
            JsonSerializer.Serialize(_remembered.Values.ToArray()));
    }

    public string Register(string tokenId, string originalCredential, string publicKey,
        TokenRegistry registry)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || tokenId.Length > 256)
            throw new ArgumentException("Invalid Admin identity.");
        var match = registry.Lookup(originalCredential);
        if (match is null || match.Value.Id != tokenId || match.Value.Role != TokenRole.Admin)
            throw new UnauthorizedAccessException("Only an active Admin API token may register a browser.");

        if (string.IsNullOrWhiteSpace(publicKey) || publicKey.Length > 1024)
            throw new ArgumentException("Invalid browser public key.");
        // Validate the public signing key before persisting it.
        var publicBytes = Convert.FromBase64String(publicKey);
        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(publicBytes, out var bytesRead);
            if (bytesRead != publicBytes.Length || verifier.KeySize != 256)
                throw new ArgumentException("Expected an ECDSA P-256 browser public key.");
        }

        var id = Generate();
        lock (_gate)
        {
            Prune(DateTimeOffset.UtcNow);
            while (_remembered.Count >= MaxSessions)
            {
                var oldest = _remembered.Values.MinBy(x => x.ExpiresAt)!;
                _remembered.Remove(oldest.Id);
            }
            _remembered[id] = new Remembered(id, tokenId, Hash(originalCredential),
                publicKey, DateTimeOffset.UtcNow.Add(RememberDuration));
            try { Save(); }
            catch
            {
                _remembered.Remove(id);
                throw;
            }
        }
        return id;
    }

    public string? CreateChallenge(string? id, TokenRegistry registry)
    {
        if (id?.Length != 43) return null;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            Prune(now);
            if (!_remembered.TryGetValue(id, out var item) ||
                !registry.IsAdminCredentialHashActive(item.TokenId, item.CredentialHash))
                return null;
            var challenge = Generate();
            _challenges[id] = new ChallengeState(challenge, now.Add(ChallengeDuration));
            return challenge;
        }
    }

    public string? Redeem(string? id, string? challenge, string? signature, TokenRegistry registry)
    {
        if (id?.Length != 43 || challenge?.Length != 43 ||
            string.IsNullOrWhiteSpace(signature) || signature.Length > 256) return null;

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            Prune(now);
            if (!_challenges.Remove(id, out var expected) ||
                !string.Equals(expected.Value, challenge, StringComparison.Ordinal) ||
                !_remembered.TryGetValue(id, out var item) ||
                !registry.IsAdminCredentialHashActive(item.TokenId, item.CredentialHash))
                return null;

            try
            {
                var publicKey = Convert.FromBase64String(item.PublicKey);
                var sig = Convert.FromBase64String(signature);
                if (sig.Length != 64) return null;
                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(publicKey, out var bytesRead);
                if (bytesRead != publicKey.Length || verifier.KeySize != 256) return null;
                var signedBytes = Encoding.UTF8.GetBytes("gsc-browser-v1:" + id + ":" + challenge);
                if (!verifier.VerifyData(signedBytes, sig, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return null;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }

            while (_access.Count >= MaxAccessTokens)
            {
                var oldest = _access.MinBy(x => x.Value.ExpiresAt);
                _access.Remove(oldest.Key);
            }
            var bearer = Generate();
            _access[Hash(bearer)] = new Access(item.TokenId, item.CredentialHash, id,
                now.Add(AccessDuration));
            return bearer;
        }
    }

    public (string Id, string Name, TokenRole Role)? Lookup(string? bearer, TokenRegistry registry)
    {
        if (string.IsNullOrEmpty(bearer) || bearer.Length != 43) return null;
        lock (_gate)
        {
            Prune(DateTimeOffset.UtcNow);
            if (!_access.TryGetValue(Hash(bearer), out var entry)) return null;
            if (!registry.IsAdminCredentialHashActive(entry.TokenId, entry.CredentialHash)) return null;
            return (entry.TokenId, "Remembered local browser", TokenRole.Admin);
        }
    }

    public void Forget(string? id)
    {
        if (id?.Length != 43) return;
        lock (_gate)
        {
            _challenges.Remove(id);
            foreach (var key in _access.Where(x => x.Value.ParentId == id)
                         .Select(x => x.Key).ToArray()) _access.Remove(key);
            if (_remembered.Remove(id)) Save();
        }
    }
}

public sealed record LocalBrowserRegistration(string? PublicKey);
public sealed record LocalBrowserIdentity(string? Id);
public sealed record LocalBrowserProof(string? Id, string? Challenge, string? Signature);

/// <summary>Only direct, same-origin Windows loopback requests may authenticate browsers.</summary>
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
}
