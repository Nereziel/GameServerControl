using System.Text.Json;
using GameServerControl.Agent.Auth;
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
    Console.WriteLine("Security token storage and authentication throttle checks OK.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
