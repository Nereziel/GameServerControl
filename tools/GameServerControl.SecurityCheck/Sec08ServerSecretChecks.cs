using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using GameServerControl.Agent.Security;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

internal static class Sec08ServerSecretChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-sec08-server-codec-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var codec = new ServerSecretProtection(new SecretProtector(root));
            var source = new ServerDef(
                "server-a", "Server A", "", GameType.SteamGeneric,
                "/srv/a/server", "/srv/a", Array.Empty<string>(), Array.Empty<string>(),
                "123", null, null, "127.0.0.1", 25575,
                "rcon-password-SEC08", HostingMode.BareMetal,
                DiscordWebhookUrl: "https://example.invalid/webhook/SEC08");

            var stored = codec.ProtectForStorage(source);
            Check(SecretProtector.IsProtected(stored.RconPassword), "RCON password was not encrypted for storage.");
            Check(SecretProtector.IsProtected(stored.DiscordWebhookUrl), "Discord webhook was not encrypted for storage.");
            Check(stored.RconPassword != source.RconPassword && stored.DiscordWebhookUrl != source.DiscordWebhookUrl,
                "Server storage codec returned reusable plaintext.");

            var loaded = codec.UnprotectFromStorage(stored, out var legacy);
            Check(!legacy, "Already encrypted server state was incorrectly marked legacy.");
            Check(loaded.RconPassword == source.RconPassword, "Encrypted RCON password did not round-trip.");
            Check(loaded.DiscordWebhookUrl == source.DiscordWebhookUrl, "Encrypted Discord webhook did not round-trip.");

            var legacyLoaded = codec.UnprotectFromStorage(source, out legacy);
            Check(legacy, "Plaintext legacy server state was not marked for migration.");
            Check(legacyLoaded.RconPassword == source.RconPassword, "Legacy plaintext RCON value changed during load.");

            var swapped = stored with { Id = "server-b" };
            var rejected = false;
            try { _ = codec.UnprotectFromStorage(swapped, out _); }
            catch (CryptographicException) { rejected = true; }
            Check(rejected, "Server-bound ciphertext was accepted after changing the server ID purpose.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
