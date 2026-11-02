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

    Console.WriteLine("Security token storage checks OK.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}
