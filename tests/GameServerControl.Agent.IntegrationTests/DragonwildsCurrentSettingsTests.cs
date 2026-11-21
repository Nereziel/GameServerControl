using GameServerControl.Agent.Config;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

/// <summary>
/// Current Dragonwilds owner access uses the in-game My Player ID. Older
/// AdminPassword/AdministratorList fields may exist in an existing INI, but
/// the Agent must neither require them nor resurrect deleted credentials.
/// </summary>
public sealed class DragonwildsCurrentSettingsTests
{
    [Fact]
    public async Task Current_fields_work_without_admin_password_and_legacy_keys_are_not_reintroduced()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ServerStore>();
        var config = scope.ServiceProvider.GetRequiredService<DragonwildsConfig>();
        var schema = scope.ServiceProvider.GetServices<IDynamicSchemaExtension>()
            .OfType<DragonwildsDynamicSchema>().Single();
        var gameDir = Path.Combine(factory.TempRoot, "dragon-current");
        Directory.CreateDirectory(gameDir);
        var executable = Path.Combine(gameDir, OperatingSystem.IsWindows() ? "dragon-game.exe" : "dragon-game.bin");
        File.WriteAllText(executable, "test fixture");
        var def = store.Add(new ServerDef("dragon-current", "Dragon Current", "",
            GameType.SteamGeneric, executable, gameDir, [], [], "4019830", null, null,
            HostingMode: HostingMode.BareMetal));

        var settings = new Dictionary<string,string>
        {
            ["OwnerId"] = "fixture-player-id",
            ["ServerName"] = "Fixture Server",
            ["DefaultWorldName"] = "Fixture World",
            ["WorldPassword"] = "join-secret",
            ["Port"] = "7777", ["BeaconPort"] = "8888"
        };
        Assert.True(await config.WriteAsync(def, settings, CancellationToken.None));

        var configPath = Path.Combine(gameDir, "RSDragonwilds", "Saved", "Config",
            OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer", "DedicatedServer.ini");
        var text = File.ReadAllText(configPath);
        Assert.Contains("OwnerId=fixture-player-id", text, StringComparison.Ordinal);
        Assert.Contains("WorldPassword=join-secret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AdminPassword=", text, StringComparison.Ordinal);
        Assert.Null(await config.ValidateForStartAsync(def, CancellationToken.None));

        var displayed = await schema.BuildAsync(def, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        Assert.NotNull(displayed);
        var fields = displayed.Section.Fields.Select(field => field.Key).ToArray();
        Assert.Contains("OwnerId", fields);
        Assert.Contains("WorldPassword", fields);
        Assert.Contains("Port", fields);
        Assert.Contains("BeaconPort", fields);
        Assert.DoesNotContain("AdminPassword", fields);
        Assert.DoesNotContain("Administrators", fields);

        // Legacy keys and game-generated metadata are preserved by a regular
        // config edit; they are not converted into live UI settings.
        File.AppendAllText(configPath,
            "AdminPassword=legacy-value\r\nAdministratorList=(legacy-admin)\r\nServerGuid=fixture-guid\r\n");
        Assert.True(await config.WriteAsync(def, new Dictionary<string,string>
        {
            ["ServerName"] = "Renamed server",
            ["AdminPassword"] = "should-not-override",
            ["AdministratorList"] = "(should-not-override)"
        }, CancellationToken.None));

        text = File.ReadAllText(configPath);
        Assert.Contains("ServerName=Renamed server", text, StringComparison.Ordinal);
        Assert.Contains("AdminPassword=legacy-value", text, StringComparison.Ordinal);
        Assert.Contains("AdministratorList=(legacy-admin)", text, StringComparison.Ordinal);
        Assert.Contains("ServerGuid=fixture-guid", text, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-override", text, StringComparison.Ordinal);

        // Simulate current game removing the old admin-password line.
        File.WriteAllText(configPath,
            File.ReadAllText(configPath).Replace("AdminPassword=legacy-value\r\n", "", StringComparison.Ordinal));
        Assert.Null(await config.ValidateForStartAsync(def, CancellationToken.None));
        Assert.DoesNotContain("AdminPassword=", File.ReadAllText(configPath), StringComparison.Ordinal);
        Assert.False((await config.ReadAsync(def, CancellationToken.None)).ContainsKey("AdminPassword"));
    }

    [Fact]
    public async Task Missing_owner_player_id_blocks_start_without_creating_admin_password()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-dw-settings-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "RSDragonwilds", "Saved", "Config",
            OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "DedicatedServer.ini"),
                "[/Script/Dominion.DedicatedServerSettings]\n" +
                "ServerName=Fixture\nDefaultWorldName=Fixture\n");
            var def = new ServerDef("fixture-dragon", "Fixture", "", GameType.SteamGeneric,
                Path.Combine(root, "server.exe"), root, [], [], "4019830", null, null,
                HostingMode: HostingMode.BareMetal);
            var config = new DragonwildsConfig(null!, null!, null!,
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DragonwildsConfig>.Instance);
            var result = await config.ValidateForStartAsync(def, CancellationToken.None);
            Assert.Contains("Owner Player ID", result ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain("Admin password", result ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
