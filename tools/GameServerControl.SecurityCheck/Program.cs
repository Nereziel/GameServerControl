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
    var throttle = new AuthenticationFailureThrottle(maxFailures: 3, window: TimeSpan.FromSeconds(30), blockDuration: TimeSpan.FromSeconds(20));
    throttle.RecordFailure("client-a", now);
    throttle.RecordFailure("client-a", now.AddSeconds(1));
    Require(!throttle.TryGetRetryAfter("client-a", now.AddSeconds(2), out _), "Client was blocked before reaching the configured failure threshold.");
    throttle.RecordFailure("client-a", now.AddSeconds(2));
    Require(throttle.TryGetRetryAfter("client-a", now.AddSeconds(3), out var retryAfter), "Client was not blocked after reaching the configured failure threshold.");
    Require(retryAfter > TimeSpan.FromSeconds(18) && retryAfter <= TimeSpan.FromSeconds(20), "Authentication throttle returned an unexpected retry interval.");
    throttle.RecordSuccess("client-a");
    Require(!throttle.TryGetRetryAfter("client-a", now.AddSeconds(4), out _), "Successful authentication did not clear the client's throttle state.");

    var rolling = new AuthenticationFailureThrottle(maxFailures: 2, window: TimeSpan.FromSeconds(10), blockDuration: TimeSpan.FromSeconds(10));
    rolling.RecordFailure("client-b", now);
    rolling.RecordFailure("client-b", now.AddSeconds(11));
    Require(!rolling.TryGetRetryAfter("client-b", now.AddSeconds(11), out _), "Failures outside the configured window were incorrectly accumulated.");

    var bounded = new AuthenticationFailureThrottle(maxFailures: 2, window: TimeSpan.FromSeconds(10), blockDuration: TimeSpan.FromSeconds(10), maxTrackedClients: 1);
    bounded.RecordFailure("client-c", now);
    bounded.RecordFailure("client-d", now);
    bounded.RecordFailure("client-d", now.AddSeconds(1));
    Require(!bounded.TryGetRetryAfter("client-d", now.AddSeconds(2), out _), "Throttle exceeded its configured client-state memory bound.");

    var concurrent = new AuthenticationFailureThrottle(maxFailures: 2, window: TimeSpan.FromSeconds(10), blockDuration: TimeSpan.FromSeconds(10), maxTrackedClients: 4);
    Parallel.For(0, 64, i =>
    {
        var key = $"concurrent-{i}";
        concurrent.RecordFailure(key, now);
        concurrent.RecordFailure(key, now.AddSeconds(1));
    });
    var blockedClients = Enumerable.Range(0, 64).Count(i => concurrent.TryGetRetryAfter($"concurrent-{i}", now.AddSeconds(2), out _));
    Require(blockedClients == 4, $"Concurrent client creation violated the hard memory bound: expected 4 tracked clients, got {blockedClients}.");
}

static void VerifyPathContainment(string root)
{
    var modsRoot = Path.Combine(root, "mods");
    Directory.CreateDirectory(modsRoot);
    var safe = PathContainment.ResolveDirectChild(modsRoot, "example-mod");
    Require(Path.GetFullPath(safe) == Path.GetFullPath(Path.Combine(modsRoot, "example-mod")), "Safe mod child did not resolve to the expected path.");
    Require(PathContainment.IsWithinRoot(modsRoot, Path.Combine(modsRoot, "nested", "file.dll")), "Nested mod file was not recognized as contained.");
    Require(!PathContainment.IsWithinRoot(modsRoot, modsRoot + "-outside" + Path.DirectorySeparatorChar + "file.dll"), "Prefix-confusion path was incorrectly accepted as contained.");
    foreach (var invalid in new[] { "", ".", "..", "../escape", "..\\escape", "nested/child", "nested\\child" })
    {
        var rejected = false;
        try { _ = PathContainment.ResolveDirectChild(modsRoot, invalid); } catch (ArgumentException) { rejected = true; }
        Require(rejected, $"Unsafe mod child '{invalid}' was accepted.");
    }
    var rooted = Path.Combine(Path.GetPathRoot(Path.GetFullPath(modsRoot))!, "escape");
    var rootedRejected = false;
    try { _ = PathContainment.ResolveDirectChild(modsRoot, rooted); } catch (ArgumentException) { rootedRejected = true; }
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
    var expectedFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    Require(File.GetUnixFileMode(file) == expectedFileMode, "Secret file mode must be 0600.");
    Require(File.GetUnixFileMode(backup) == expectedFileMode, "Secret backup file mode must be 0600.");
    var expectedDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    Require(File.GetUnixFileMode(stateDir) == expectedDirMode, "Secret state directory mode must be 0700.");
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
    try { _ = protector.Unprotect(protectedValue, "test:other"); } catch (CryptographicException) { wrongPurposeRejected = true; }
    Require(wrongPurposeRejected, "Purpose-bound AES-GCM ciphertext was accepted under another purpose.");

    var payload = protectedValue[SecretProtector.ValuePrefix.Length..];
    var bytes = Convert.FromBase64String(payload);
    bytes[^1] ^= 0x01;
    var tampered = SecretProtector.ValuePrefix + Convert.ToBase64String(bytes);
    var tamperRejected = false;
    try { _ = protector.Unprotect(tampered, purpose); } catch (CryptographicException) { tamperRejected = true; }
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

static void VerifyConfigurationSecrets(string root)
{
    var configRoot = Path.Combine(root, "configuration-secrets");
    Directory.CreateDirectory(configRoot);
    const string apiToken = "bootstrap-api-token-012345678901234567890123";
    const string certPassword = "certificate-password-SEC08";
    const string guestPassword = "hyperv-password-SEC08";
    const string runtimeOnlyApiKey = "runtime-only-provider-key-SEC08";
    var appsettingsPath = Path.Combine(configRoot, "appsettings.json");
    File.WriteAllText(appsettingsPath, $$"""
    {
      "Agent": {
        "ApiToken": "{{apiToken}}",
        "CertPassword": "{{certPassword}}",
        "CurseForge": { "ApiKey": "" },
        "GuestCredentials": { "default": { "Username": "Administrator", "Password": "{{guestPassword}}" } }
      }
    }
    """);

    var cfg = new ConfigurationBuilder()
        .SetBasePath(configRoot)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Agent:CurseForge:ApiKey"] = runtimeOnlyApiKey })
        .Build();
    var protector = new SecretProtector(configRoot);
    var secrets = new ConfigurationSecretStore(configRoot, protector);
    secrets.MigrateAndApply(cfg);

    Require(cfg["Agent:ApiToken"] == apiToken, "Migrated bootstrap token is unavailable in runtime configuration.");
    Require(cfg["Agent:CertPassword"] == certPassword, "Migrated certificate password is unavailable in runtime configuration.");
    Require(cfg["Agent:GuestCredentials:default:Password"] == guestPassword, "Migrated guest password is unavailable in runtime configuration.");
    Require(cfg["Agent:CurseForge:ApiKey"] == runtimeOnlyApiKey, "Higher-precedence runtime configuration was overwritten during migration.");

    var diskConfig = File.ReadAllText(appsettingsPath);
    foreach (var plaintext in new[] { apiToken, certPassword, guestPassword })
        Require(!diskConfig.Contains(plaintext, StringComparison.Ordinal), "appsettings.json retained reusable plaintext after migration.");
    Require(diskConfig.Contains(ConfigurationSecretStore.Sentinel, StringComparison.Ordinal), "appsettings.json was not replaced with the encrypted-state sentinel.");

    var statePath = Path.Combine(configRoot, "secret-state.json");
    var encryptedState = File.ReadAllText(statePath);
    foreach (var plaintext in new[] { apiToken, certPassword, guestPassword, runtimeOnlyApiKey })
        Require(!encryptedState.Contains(plaintext, StringComparison.Ordinal), "secret-state.json contains plaintext or persisted a runtime-only override.");
    Require(encryptedState.Contains(SecretProtector.ValuePrefix, StringComparison.Ordinal), "secret-state.json contains no protected envelopes.");

    var reloadedCfg = new ConfigurationBuilder().SetBasePath(configRoot).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false).Build();
    var reloadedStore = new ConfigurationSecretStore(configRoot, new SecretProtector(configRoot));
    reloadedStore.MigrateAndApply(reloadedCfg);
    Require(reloadedCfg["Agent:ApiToken"] == apiToken, "Encrypted bootstrap token did not survive process-style reload.");
    Require(reloadedCfg["Agent:CertPassword"] == certPassword, "Encrypted certificate password did not survive process-style reload.");
    Require(reloadedCfg["Agent:GuestCredentials:default:Password"] == guestPassword, "Encrypted guest password did not survive process-style reload.");
    Require(string.IsNullOrEmpty(reloadedCfg["Agent:CurseForge:ApiKey"]), "Runtime-only provider key was incorrectly persisted to encrypted state.");

    var firstRoot = Path.Combine(root, "first-run-secret");
    Directory.CreateDirectory(firstRoot);
    File.WriteAllText(Path.Combine(firstRoot, "appsettings.json"), "{\"Agent\":{\"ApiToken\":\"REPLACE_WITH_32_CHARS+\"}}");
    var firstCfg = new ConfigurationBuilder().SetBasePath(firstRoot).AddJsonFile("appsettings.json", optional: false, reloadOnChange: false).Build();
    var firstStore = new ConfigurationSecretStore(firstRoot, new SecretProtector(firstRoot));
    firstStore.MigrateAndApply(firstCfg);
    var logger = LoggerFactory.Create(_ => { }).CreateLogger("FirstRunTokenGenerator.SecurityCheck");
    Require(FirstRunTokenGenerator.EnsureToken(firstCfg, firstRoot, firstStore, logger, out var generated), "First run did not generate an API token.");
    Require(generated.Length >= 32 && firstCfg["Agent:ApiToken"] == generated, "Generated bootstrap token was not applied in memory.");
    Require(!File.ReadAllText(Path.Combine(firstRoot, "appsettings.json")).Contains(generated, StringComparison.Ordinal), "First-run token leaked to appsettings.json.");
    Require(!File.ReadAllText(Path.Combine(firstRoot, "secret-state.json")).Contains(generated, StringComparison.Ordinal), "First-run token leaked to secret-state.json plaintext.");
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
    var legacyJson = JsonSerializer.Serialize(new[] { new { Id = "legacy-user", Name = "Legacy user", Token = legacyToken, Role = "ReadOnly", CreatedAt = DateTimeOffset.UtcNow } }, new JsonSerializerOptions { WriteIndented = true });
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
    VerifyConfigurationSecrets(root);
    Console.WriteLine("Security token storage, auth throttle, path containment, file permissions, AES-GCM, and configuration-secret checks OK.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
