using GameServerControl.Agent.Config;
using GameServerControl.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ManagedConfigPasswordTests
{
    [Fact]
    public void Managed_config_response_restores_canonical_password_keys_for_all_supported_games()
    {
        var cases = new[]
        {
            (ConfigSchemas.Valheim, "password"),
            (ConfigSchemas.Palworld, "AdminPassword"),
            (ConfigSchemas.Palworld, "ServerPassword"),
            (ConfigSchemas.Windrose, "Password"),
            (ConfigSchemas.Satisfactory, "ClientPassword")
        };
        foreach (var (schema, key) in cases)
        {
            var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [key.ToUpperInvariant()] = "fixture-value"
            };
            var normalized = ManagedConfigValues.Canonicalize(schema, raw);
            Assert.Equal("fixture-value", normalized[key]);
            Assert.Contains(key, normalized.Keys);
            Assert.Single(normalized);
        }
    }

    [Fact]
    public void Satisfactory_write_only_password_requires_explicit_clear_intent()
    {
        Assert.Null(SatisfactoryConfig.ResolveClientPasswordUpdate(
            new Dictionary<string, string> { ["ClientPassword"] = "" }));
        Assert.Null(SatisfactoryConfig.ResolveClientPasswordUpdate(
            new Dictionary<string, string>()));
        Assert.Equal("fixture-new-password", SatisfactoryConfig.ResolveClientPasswordUpdate(
            new Dictionary<string, string> { ["ClientPassword"] = "fixture-new-password" }));
        Assert.Equal("", SatisfactoryConfig.ResolveClientPasswordUpdate(
            new Dictionary<string, string> { ["ClientPassword"] = "", ["ClearClientPassword"] = "true" }));
        Assert.Throws<ArgumentException>(() => SatisfactoryConfig.ResolveClientPasswordUpdate(
            new Dictionary<string, string> { ["ClientPassword"] = "fixture-new-password", ["ClearClientPassword"] = "true" }));
    }

    [Fact]
    public async Task Dragonwilds_admin_password_survives_ini_reload_with_case_variant_key()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-dragon-config-" + Guid.NewGuid().ToString("N"));
        var platform = OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer";
        var configDir = Path.Combine(root, "RSDragonwilds", "Saved", "Config", platform);
        Directory.CreateDirectory(configDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(configDir, "DedicatedServer.ini"),
                "[/Script/Dominion.DedicatedServerSettings]\n" +
                "ownerid=fixture-owner\nServerName=fixture-server\n" +
                "DefaultWorldName=fixture-world\nadminpassword=fixture-admin-secret\n");
            var def = new ServerDef("dragon", "Dragon", "", GameType.SteamGeneric,
                Path.Combine(root, "server.exe"), root, [], [], "4019830", null, null,
                HostingMode: HostingMode.BareMetal);
            var config = new DragonwildsConfig(null!, null!, null!, new ConfigurationBuilder().Build(),
                NullLogger<DragonwildsConfig>.Instance);
            var read = await config.ReadAsync(def, CancellationToken.None);
            var schema = new ConfigSchema("dragon", "Dragon", [
                new ConfigSection("Server", [
                    new ConfigField("OwnerId", "Owner ID", ConfigFieldType.Text),
                    new ConfigField("AdminPassword", "Admin password", ConfigFieldType.Password)
                ])
            ]);
            var normalized = ManagedConfigValues.Canonicalize(schema, read);
            Assert.Equal("fixture-admin-secret", normalized["AdminPassword"]);
            Assert.Contains("AdminPassword", normalized.Keys);
            Assert.Equal("fixture-owner", normalized["OwnerId"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
