using GameServerControl.Agent.Config;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class DragonwildsPasswordPersistenceTests
{
    [Fact]
    public void Recovery_copy_is_encrypted_and_survives_agent_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gsc-dw-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = new DragonwildsPasswordStore(dir);
            first.Save("my-server", "fixture-admin-secret");
            var written = Directory.GetFiles(Path.Combine(dir, "dragonwilds-passwords"), "*.secret");
            Assert.Single(written);
            var storedText = File.ReadAllText(written[0]);
            Assert.StartsWith("enc:v1:", storedText, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-admin-secret", storedText, StringComparison.Ordinal);
            Assert.Equal("fixture-admin-secret", new DragonwildsPasswordStore(dir).Load("my-server"));
            Assert.Null(first.Load("other-server"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Config_save_retains_password_and_restores_it_if_the_game_removes_ini_key()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ServerStore>();
        var config = scope.ServiceProvider.GetRequiredService<DragonwildsConfig>();
        var recovery = scope.ServiceProvider.GetRequiredService<DragonwildsPasswordStore>();
        var gameDir = Path.Combine(factory.TempRoot, "dragon-game");
        Directory.CreateDirectory(gameDir);
        var executable = Path.Combine(gameDir, OperatingSystem.IsWindows() ? "dragon-game.exe" : "dragon-game.bin");
        File.WriteAllText(executable, "not a running process");
        var def = store.Add(new ServerDef("dragon-test", "Dragon Test", "",
            GameType.SteamGeneric, executable, gameDir, [], [], "4019830", null, null,
            HostingMode: HostingMode.BareMetal));
        var settings = new Dictionary<string,string>
        {
            ["OwnerId"]="fixture-owner", ["ServerName"]="fixture-world",
            ["DefaultWorldName"]="fixture-world", ["AdminPassword"]="fixture-admin-secret",
            ["Port"]="7777", ["BeaconPort"]="8888"
        };
        Assert.True(await config.WriteAsync(def, settings, CancellationToken.None));
        Assert.Equal("fixture-admin-secret", recovery.Load(def.Id));

        // Browsers/password managers sometimes submit a blank field on an
        // otherwise normal config save. That must not erase AdminPassword.
        Assert.True(await config.WriteAsync(def, new Dictionary<string,string>
        {
            ["AdminPassword"]="", ["ServerName"]="fixture-updated"
        }, CancellationToken.None));

        var path = Path.Combine(gameDir, "RSDragonwilds", "Saved", "Config",
            OperatingSystem.IsWindows() ? "WindowsServer" : "LinuxServer", "DedicatedServer.ini");
        Assert.Contains("AdminPassword=fixture-admin-secret", File.ReadAllText(path), StringComparison.Ordinal);

        // Simulate a game build clearing the INI field while running.
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            "AdminPassword=fixture-admin-secret", "AdminPassword=", StringComparison.Ordinal));
        var loaded = await config.ReadAsync(def, CancellationToken.None);
        Assert.Equal("fixture-admin-secret", loaded["AdminPassword"]);

        // Before the next Start the Agent restores it while the process is stopped.
        Assert.Null(await config.ValidateForStartAsync(def, CancellationToken.None));
        Assert.Contains("AdminPassword=fixture-admin-secret", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
