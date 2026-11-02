using System.Security.Cryptography;
using System.Text.Json;
using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Security;
using GameServerControl.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static TokenRegistry Registry(string dataDir)
{
    var cfg = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:DataDir"] = dataDir,
            ["Agent:ApiToken"] = ""
        })
        .Build();
    var logger = LoggerFactory.Create(_ => { }).CreateLogger<TokenRegistry>();
    return new TokenRegistry(cfg, logger);
}

static void VerifyAuthenticationThrottle()
{
    var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    var throttle = new AuthenticationFailureThrottle(
        maxFailures: 3,
        window: TimeSpan.FromSeconds(30),
        blockDuration: TimeSpan.FromSeconds(20));

    throttle.RecordFailure("client-a", now);
    throttle.RecordFailure("client-a", now.AddSeconds(1));
    Require(!throttle.TryGetRetryAfter("client-a", now.AddSeconds(2), out _),
        "Client was blocked before reaching the configured failure threshold.");

    throttle.RecordFailure("client-a", now.AddSeconds(2));
    Require(throttle.TryGetRetryAfter("client-a", now.AddSeconds(3), out var retryAfter),
        "Client was not blocked after reaching the configured failure threshold.");
    Require(retryAfter > TimeSpan.FromSeconds(18) && retryAfter <= TimeSpan.FromSeconds(20),
        "Authentication throttle returned an unexpected retry interval.");

    throttle.RecordSuccess("client-a");
    Require(!throttle.TryGetRetryAfter("client-a", now.AddSeconds(4), out _),
        "Successful authentication did not clear the client's throttle state.");

    var rolling = new AuthenticationFailureThrottle(
        maxFailures: 2,
        window: TimeSpan.FromSeconds(10),
        blockDuration: TimeSpan.FromSeconds(10));
    rolling.RecordFailure("client-b", now);
    rolling.RecordFailure("client-b", now.AddSeconds(11));
    Require(!rolling.TryGetRetryAfter("client-b", now.AddSeconds(11), out _),
        "Failures outside the configured window were incorrectly accumulated.");

    var bounded = new AuthenticationFailureThrottle(
        maxFailures: 2,
        window: TimeSpan.FromSeconds(10),
        blockDuration: TimeSpan.FromSeconds(10),
        maxTrackedClients: 1);
    bounded.RecordFailure("client-c", now);
    bounded.RecordFailure("client-d", now);
    bounded.RecordFailure("client-d", now.AddSeconds(1));
    Require(!bounded.TryGetRetryAfter("client-d", now.AddSeconds(2), out _),
        "Throttle exceeded its configured client-state memory bound.");

    var concurrent = new AuthenticationFailureThrottle(
        maxFailures: 2,
        window: TimeSpan.FromSeconds(10),
        blockDuration: TimeSpan.FromSeconds(10),
        maxTrackedClients: 4);
    Parallel.For(0, 64, i =>
    {
        var key = $"concurrent-{i}";
        concurrent.RecordFailure(key, now);
        concurrent.RecordFailure(key, now.AddSeconds(1));
    });
    var blockedClients = Enumerable.Range(0, 64)
        .Count(i => concurrent.TryGetRetryAfter($"concurrent-{i}", now.AddSeconds(2), out _));
    Require(blockedClients == 4,
        $"Concurrent client creation violated the hard memory bound: expected 4 tracked clients, got {blockedClients}.");
}

static void VerifyPathContainment(string root)
{
    var modsRoot = Path.Combine(root, "mods");
    Directory.CreateDirectory(modsRoot);

    var safe = PathContainment.ResolveDirectChild(modsRoot, "example-mod");
    Require(Path.GetFullPath(safe) == Path.GetFullPath(Path.Combine(modsRoot, "example-mod")),
        "Safe mod child did not resolve to the expected path.");
    Require(PathContainment.IsWithinRoot(modsRoot, Path.Combine(modsRoot, "nested", "file.dll")),
        "Nested mod file was not recognized as contained.");
    Require(!PathContainment.IsWithinRoot(modsRoot, modsRoot + "-outside" + Path.DirectorySeparatorChar + "file.dll"),
        "Prefix-confusion path was incorrectly accepted as contained.");

    foreach (var invalid in new[] { "", ".", "..", "../escape", "..\\escape", "nested/child", "nested\\child" })
    {
        var rejected = false;
        try { _ = PathContainment.ResolveDirectChild(modsRoot, invalid); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected, $"Unsafe mod child '{invalid}' was accepted.");
    }

    var rooted = Path.Combine(Path.GetPathRoot(Path.GetFullPath(modsRoot))!, "escape");
    var rootedRejected = false;
    try { _ = PathContainment.ResolveDirectChild(modsRoot, rooted); }
    catch (ArgumentException) { rootedRejected = true; }
    Require(rootedRejected, "Rooted mod child was accepted.");
}

static void VerifySecretFilePermissions(string root)
{
    var stateDir = Path.Combine(root, "secret-state");
    var file = Path.Combine(stateDir, "state.json");
    var backup = Path.Combine(stateDir, "state.json.bak");
    const string first = "{\"secret\":true}";
    const string second = "{\"secret\":false}";

    SecretFilePermissions.WriteAllTextAtomic(file, first);
    Require(File.ReadAllText(file) == first, "Atomic secret-state write changed file contents.");

    SecretFilePermissions.WriteAllTextAtomic(file, second, backup);
    Require(File.ReadAllText(file) == second, "Atomic secret-state replacement did not persist the new contents.");
    Require(File.ReadAllText(backup) == first, "Atomic secret-state replacement did not preserve the previous contents in the backup.");

    if (OperatingSystem.IsWindows()) return;

    var fileMode = File.GetUnixFileMode(file);
    var expectedFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    Require(fileMode == expectedFileMode, $"Secret file mode must be 0600, got {fileMode}.");
    Require(File.GetUnixFileMode(backup) == expectedFileMode, "Secret backup file mode must be 0600.");

    var dirMode = File.GetUnixFileMode(stateDir);
    var expectedDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    Require(dirMode == expectedDirMode, $"Secret state directory mode must be 0700, got {dirMode}.");

    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    SecretFilePermissions.RestrictExisting(file);
    Require(File.GetUnixFileMode(file) == expectedFileMode, "RestrictExisting did not restore secret file mode 0600.");
}

static void VerifySecretProtector(string root)
{
    var secretRoot = Path.Combine(root, "secret-protector");
    Directory.CreateDirectory(secretRoot);
    var protector = new SecretProtector(secretRoot);
    const string plaintext = "synthetic-secret-value-SEC08";
    const string purpose = "test:primary";

    var protectedValue = protector.Protect(plaintext, purpose);
    Require(SecretProtector.IsProtected(protectedValue), "Protected value does not carry the SEC-08 envelope marker.");
    Require(!protectedValue.Contains(plaintext, StringComparison.Ordinal), "Protected envelope contains plaintext secret material.");
    Require(protector.Unprotect(protectedValue, purpose) == plaintext, "Secret protector round-trip failed.");

    var wrongPurposeRejected = false;
    try { _ = protector.Unprotect(protectedValue, "test:other"); }
    catch (CryptographicException) { wrongPurposeRejected = true; }
    Require(wrongPurposeRejected, "Purpose-bound AES-GCM ciphertext was accepted under another purpose.");

    var payload = protectedValue[SecretProtector.ValuePrefix.Length..];
    var bytes = Convert.FromBase64String(payload);
    bytes[^1] ^= 0x01;
    var tampered = SecretProtector.ValuePrefix + Convert.ToBase64String(bytes);
    var tamperRejected = false;
    try { _ = protector.Unprotect(tampered, purpose); }
    catch (CryptographicException) { tamperRejected = true; }
    Require(tamperRejected, "Tampered AES-GCM ciphertext was accepted.");

    var keyPath = Path.Combine(secretRoot, "secret.key");
    var keyEnvelope = File.ReadAllText(keyPath);
    if (OperatingSystem.IsWindows())
        Require(keyEnvelope.StartsWith("gsc-key-v1:dpapi:", StringComparison.Ordinal), "Windows secret.key is not DPAPI LocalMachine wrapped.");
    else
    {
        Require(keyEnvelope.StartsWith("gsc-key-v1:raw:", StringComparison.Ordinal), "Linux secret.key has an unexpected envelope format.");
        Require(File.GetUnixFileMode(keyPath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Linux secret.key must be mode 0600.");
    }

    var brokenRoot = Path.Combine(root, "broken-secret-key");
    Directory.CreateDirectory(brokenRoot);
    var brokenKeyPath = Path.Combine(brokenRoot, "secret.key");
    SecretFilePermissions.WriteAllTextAtomic(brokenKeyPath, "gsc-key-v1:raw:not-valid-base64!");
    var brokenRejected = false;
    try { _ = new SecretProtector(brokenRoot); }
    catch (Exception ex) when (ex is InvalidOperationException or FormatException or CryptographicException) { brokenRejected = true; }
    Require(brokenRejected, "Malformed existing master key was silently replaced instead of failing closed.");
    Require(File.ReadAllText(brokenKeyPath) == "gsc-key-v1:raw:not-valid-base64!", "Malformed existing master key was overwritten.");
}

var root = Path.Combine(Path.GetTempPath(), "gsc-security-check-" + Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(root);
try
{
    var registry = Registry(root);
    var created = registry.Create("ops-admin", "Ops admin", TokenRole.Admin);
    Require(!string.IsNullOrWhiteSpace(created.Token), "Create must return the plaintext token exactly once.");
    Require(registry.Lookup(created.Token) is { Role: TokenRole.Admin }, "Fresh token lookup failed.");

    var tokenFile = Path.Combine(root, "tokens.json");
    var stored = File.ReadAllText(tokenFile);
    Require(!stored.Contains(created.Token, StringComparison.Ordinal), "tokens.json contains a reusable plaintext bearer token.");
    Require(stored.Contains("TokenHash", StringComparison.Ordinal), "tokens.json does not persist TokenHash.");
    Require(stored.Contains("TokenPrefix", StringComparison.Ordinal), "tokens.json does not persist TokenPrefix.");

    var reloaded = Registry(root);
    Require(reloaded.Lookup(created.Token) is { Id: "ops-admin", Role: TokenRole.Admin }, "Hashed token lookup failed after reload.");

    var legacyRoot = Path.Combine(root, "legacy");
    Directory.CreateDirectory(legacyRoot);
    const string legacyToken = "legacy-token-value-012345678901234567890123";
    var legacyJson = JsonSerializer.Serialize(new[]
    {
        new
        {
            Id = "legacy-user",
            Name = "Legacy user",
            Token = legacyToken,
            Role = "ReadOnly",
            CreatedAt = DateTimeOffset.UtcNow
        }
    }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(Path.Combine(legacyRoot, "tokens.json"), legacyJson);

    var migrated = Registry(legacyRoot);
    Require(migrated.Lookup(legacyToken) is { Id: "legacy-user", Role: TokenRole.ReadOnly }, "Migrated legacy token no longer authenticates.");
    var migratedJson = File.ReadAllText(Path.Combine(legacyRoot, "tokens.json"));
    Require(!migratedJson.Contains(legacyToken, StringComparison.Ordinal), "Legacy plaintext token was not removed during migration.");
    Require(migratedJson.Contains("TokenHash", StringComparison.Ordinal), "Migrated token does not contain TokenHash.");

    VerifyAuthenticationThrottle();
    VerifyPathContainment(root);
    VerifySecretFilePermissions(root);
    VerifySecretProtector(root);
    Console.WriteLine("Security token storage, authentication throttle, path containment, secret file permission, and secret protector checks OK.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
