using System.Security.Cryptography;
using System.Text.Json;
using GameServerControl.Agent.Networking;
using GameServerControl.Agent.Security;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "gsc-secret-protection-check-" + Guid.NewGuid().ToString("n"));
Directory.CreateDirectory(root);
try
{
    const string secret = "sec08-secret-value-0123456789";
    var protector = new SecretProtector(root);
    var protectedValue = protector.Protect(secret);
    Require(protector.IsProtected(protectedValue), "Protected value is missing the versioned envelope marker.");
    Require(!protectedValue.Contains(secret, StringComparison.Ordinal), "Protected envelope contains plaintext secret material.");
    Require(protector.Unprotect(protectedValue) == secret, "Secret protection round-trip failed.");

    var tampered = protectedValue[..^1] + (protectedValue[^1] == 'A' ? 'B' : 'A');
    var tamperRejected = false;
    try { _ = protector.Unprotect(tampered); }
    catch (CryptographicException) { tamperRejected = true; }
    catch (FormatException) { tamperRejected = true; }
    Require(tamperRejected, "Tampered secret envelope was accepted.");

    if (!OperatingSystem.IsWindows())
    {
        var keyPath = Path.Combine(root, "secret-protection.key");
        Require(File.Exists(keyPath), "Linux secret protector did not create its local master-key file.");
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Require(File.GetUnixFileMode(keyPath) == expected, "Linux master-key file must be mode 0600.");
    }

    var appsettingsPath = Path.Combine(root, "appsettings.json");
    var original = new
    {
        Agent = new
        {
            ApiToken = "bootstrap-admin-token-0123456789012345",
            CertPassword = "certificate-password",
            PublicRelay = new { ApiToken = "relay-token-01234567890123456789" },
            CurseForge = new { ApiKey = "curseforge-api-key" },
            GuestCredentials = new Dictionary<string, object>
            {
                ["default"] = new { Username = "Administrator", Password = "guest-password" }
            }
        }
    };
    File.WriteAllText(appsettingsPath, JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }));

    var cfg = new ConfigurationBuilder()
        .SetBasePath(root)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .Build();
    AppSettingsSecretProtection.ProtectAndLoad(cfg, appsettingsPath, protector);

    Require(cfg["Agent:ApiToken"] == "bootstrap-admin-token-0123456789012345", "Runtime bootstrap token was not decrypted.");
    Require(cfg["Agent:CertPassword"] == "certificate-password", "Runtime certificate password was not decrypted.");
    Require(cfg["Agent:PublicRelay:ApiToken"] == "relay-token-01234567890123456789", "Runtime relay token was not decrypted.");
    Require(cfg["Agent:CurseForge:ApiKey"] == "curseforge-api-key", "Runtime CurseForge API key was not decrypted.");
    Require(cfg["Agent:GuestCredentials:default:Password"] == "guest-password", "Runtime guest password was not decrypted.");

    var storedSettings = File.ReadAllText(appsettingsPath);
    foreach (var plaintext in new[]
    {
        "bootstrap-admin-token-0123456789012345",
        "certificate-password",
        "relay-token-01234567890123456789",
        "curseforge-api-key",
        "guest-password"
    })
        Require(!storedSettings.Contains(plaintext, StringComparison.Ordinal), $"appsettings.json still contains plaintext secret: {plaintext}");
    Require(storedSettings.Contains("enc:v1:", StringComparison.Ordinal), "appsettings.json does not contain versioned encrypted envelopes.");

    var def = new ServerDef(
        Id: "valheim-1",
        Name: "Valheim",
        VmName: "",
        GameType: GameType.SteamGeneric,
        GuestExePath: "/srv/valheim/server.x86_64",
        GuestWorkingDir: "/srv/valheim",
        StartArgs: Array.Empty<string>(),
        SaveDirs: Array.Empty<string>(),
        SteamAppId: "896660",
        GuestCredentialId: null,
        LogPathInGuest: null,
        RconPassword: "rcon-password",
        HostingMode: HostingMode.BareMetal,
        DiscordWebhookUrl: "https://discord.invalid/api/webhooks/secret");

    var persistedDef = ServerSecretStorage.ProtectForStorage(def, protector);
    Require(persistedDef.RconPassword is not null && protector.IsProtected(persistedDef.RconPassword), "RCON password was not protected for storage.");
    Require(persistedDef.DiscordWebhookUrl is not null && protector.IsProtected(persistedDef.DiscordWebhookUrl), "Discord webhook was not protected for storage.");
    var runtimeDef = ServerSecretStorage.UnprotectFromStorage(persistedDef, protector);
    Require(runtimeDef.RconPassword == def.RconPassword, "RCON password round-trip failed.");
    Require(runtimeDef.DiscordWebhookUrl == def.DiscordWebhookUrl, "Discord webhook round-trip failed.");

    var serversPath = Path.Combine(root, "servers.json");
    var serverFileJson = JsonSerializer.Serialize(new { Servers = new[] { def } }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(serversPath, serverFileJson);
    File.WriteAllText(serversPath + ".bak", serverFileJson);
    var persistenceCfg = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:DataDir"] = root,
            ["Agent:ServersJsonPath"] = serversPath,
            ["Agent:PublicRelay:StatePath"] = Path.Combine(root, "public-relay.json")
        })
        .Build();
    using var loggerFactory = LoggerFactory.Create(_ => { });
    using var fileProvider = new PhysicalFileProvider(root);
    var environment = new TestHostEnvironment(root, fileProvider);
    var registry = new ServerRegistry(persistenceCfg, environment, loggerFactory.CreateLogger<ServerRegistry>());
    Require(registry.Get(def.Id)?.RconPassword == def.RconPassword, "ServerRegistry did not expose decrypted RCON credentials at runtime.");
    Require(registry.Get(def.Id)?.DiscordWebhookUrl == def.DiscordWebhookUrl, "ServerRegistry did not expose decrypted webhook at runtime.");
    foreach (var path in new[] { serversPath, serversPath + ".bak" })
    {
        var stored = File.ReadAllText(path);
        Require(!stored.Contains("rcon-password", StringComparison.Ordinal), $"{Path.GetFileName(path)} still contains plaintext RCON password.");
        Require(!stored.Contains("https://discord.invalid/api/webhooks/secret", StringComparison.Ordinal), $"{Path.GetFileName(path)} still contains plaintext webhook.");
        Require(stored.Contains("enc:v1:", StringComparison.Ordinal), $"{Path.GetFileName(path)} was not migrated to encrypted envelopes.");
    }

    const string relayToken = "relay-state-token-01234567890123456789";
    var relayStatePath = Path.Combine(root, "public-relay.json");
    File.WriteAllText(relayStatePath, JsonSerializer.Serialize(new
    {
        Global = new
        {
            Url = "https://relay.invalid",
            ApiToken = relayToken,
            TargetAddress = "100.64.0.10",
            PublicAddress = "203.0.113.10"
        },
        Servers = new Dictionary<string, object>()
    }, new JsonSerializerOptions { WriteIndented = true }));
    var relay = new PublicRelayManager(persistenceCfg, environment, registry, loggerFactory.CreateLogger<PublicRelayManager>());
    Require(relay.Configured, "PublicRelayManager lost its decrypted relay token during migration.");
    var storedRelay = File.ReadAllText(relayStatePath);
    Require(!storedRelay.Contains(relayToken, StringComparison.Ordinal), "public-relay.json still contains a plaintext relay API token.");
    Require(storedRelay.Contains("enc:v1:", StringComparison.Ordinal), "public-relay.json was not migrated to an encrypted token envelope.");

    Console.WriteLine("SEC-08 encrypted-at-rest behavior and persistence wiring checks OK.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

sealed class TestHostEnvironment(string root, IFileProvider provider) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "GameServerControl.SecretProtectionCheck";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = provider;
}
