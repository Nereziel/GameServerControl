using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ServerSettingsConcurrencyTests
{
    [Fact]
    public void Retention_update_does_not_overwrite_configuration_saved_since_registry_snapshot()
    {
        using var fixture = new Fixture();
        var stale = fixture.Registry.Get("alpha")!;

        // Reproduces the original race: BackupService once used the cached
        // ServerRegistry snapshot to replace the entire ServerDef.
        fixture.Store.Update("alpha", stale with { Name = "New server name", RconPort = 25580 });
        var saved = fixture.Backups.SaveSettings("alpha", 7);

        Assert.Equal(7, saved.Retention);
        Assert.Equal("New server name", fixture.Registry.Get("alpha")!.Name);
        Assert.Equal(25580, fixture.Registry.Get("alpha")!.RconPort);
        Assert.Equal(7, fixture.Registry.Get("alpha")!.BackupRetention);
        AssertPersisted(fixture, "New server name", 7);
    }

    [Fact]
    public void Full_definition_edit_does_not_reset_dedicated_settings_from_stale_snapshot()
    {
        using var fixture = new Fixture(steam: true);
        var stale = fixture.Registry.Get("alpha")!;

        fixture.Backups.SaveSettings("alpha", 15);
        fixture.Store.SetAutoUpdatePreferences("alpha", onStart: true, onRestart: true);

        // The general editor received an older server snapshot and did not
        // edit any settings owned by their dedicated endpoints.
        var updated = fixture.Store.Update("alpha", stale with { Name = "Edited safely" });

        Assert.Equal("Edited safely", updated.Name);
        Assert.Equal(15, updated.BackupRetention);
        Assert.True(updated.AutoUpdateOnStart);
        Assert.True(updated.AutoUpdateOnRestart);
        Assert.Equal(15, fixture.Backups.GetSettings("alpha").Retention);
        AssertPersisted(fixture, "Edited safely", 15);
    }

    [Fact]
    public async Task Concurrent_full_edit_retention_and_auto_update_keep_all_changes()
    {
        using var fixture = new Fixture(steam: true);

        // Run both orders repeatedly. Each edit uses a snapshot obtained
        // before other tasks begin, exposing the old lost-update scenario.
        for (var i = 1; i <= 16; i++)
        {
            var stale = fixture.Registry.Get("alpha")!;
            var name = "Renamed server " + i;
            var retention = i + 3;
            var onStart = i % 2 == 0;
            var onRestart = i % 3 == 0;

            var calls = new Action[]
            {
                () => fixture.Store.Update("alpha", stale with { Name = name }),
                () => fixture.Backups.SaveSettings("alpha", retention),
                () => fixture.Store.SetAutoUpdatePreferences("alpha", onStart, onRestart)
            };
            await Task.WhenAll(calls.Select(call => Task.Run(call)));

            var actual = fixture.Registry.Get("alpha")!;
            Assert.Equal(name, actual.Name);
            Assert.Equal(retention, actual.BackupRetention);
            Assert.Equal(onStart, actual.AutoUpdateOnStart);
            Assert.Equal(onRestart, actual.AutoUpdateOnRestart);
            AssertPersisted(fixture, name, retention);
        }
    }

    [Fact]
    public async Task Concurrent_api_puts_preserve_retention_when_edit_form_is_stale()
    {
        using var fixture = new Fixture();
        var old = fixture.Registry.Get("alpha")!;

        var settingsTask = fixture.Client.PutAsJsonAsync(
            "/api/servers/alpha/backups/settings", new BackupSettings(9));
        var editorTask = fixture.Client.PutAsJsonAsync(
            "/api/servers/alpha", old with { Name = "HTTP-edited server" });

        using var settingsResult = await settingsTask;
        using var editorResult = await editorTask;
        Assert.Equal(HttpStatusCode.OK, settingsResult.StatusCode);
        Assert.Equal(HttpStatusCode.OK, editorResult.StatusCode);

        var editorResponse = await editorResult.Content.ReadFromJsonAsync<ServerDef>();
        Assert.NotNull(editorResponse);
        Assert.Equal("HTTP-edited server", editorResponse.Name);
        Assert.Equal("HTTP-edited server", fixture.Registry.Get("alpha")!.Name);
        Assert.Equal(9, fixture.Registry.Get("alpha")!.BackupRetention);
        AssertPersisted(fixture, "HTTP-edited server", 9);
    }

    [Fact]
    public void Retention_validation_and_missing_id_do_not_mutate_other_servers()
    {
        using var fixture = new Fixture();
        var original = fixture.Registry.Get("alpha")!;
        Assert.Throws<ArgumentException>(() => fixture.Store.SetBackupRetention("alpha", 0));
        Assert.Throws<ArgumentException>(() => fixture.Store.SetBackupRetention("alpha", 201));
        Assert.Throws<KeyNotFoundException>(() => fixture.Store.SetBackupRetention("absent", 7));
        Assert.Equal(original.Name, fixture.Registry.Get("alpha")!.Name);
        Assert.Equal(original.BackupRetention, fixture.Registry.Get("alpha")!.BackupRetention);
        Assert.Null(fixture.Registry.Get("beta")!.BackupRetention);
    }

    private static void AssertPersisted(Fixture fixture, string name, int retention)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Factory.ServersPath));
        var stored = document.RootElement.GetProperty("Servers").EnumerateArray()
            .Single(x => x.GetProperty("Id").GetString() == "alpha");
        Assert.Equal(name, stored.GetProperty("Name").GetString());
        Assert.Equal(retention, stored.GetProperty("BackupRetention").GetInt32());
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool steam = false)
        {
            Factory = new AgentTestFactory(alphaUsesStdinProfile: steam);
            // The fixture's placeholder webhook is intentionally not a valid
            // Discord URL. The full-definition editor validates it, so remove
            // it here without changing the production validation contract.
            var doc = JsonNode.Parse(File.ReadAllText(Factory.ServersPath))!;
            doc["Servers"]![0]!["DiscordWebhookUrl"] = null;
            File.WriteAllText(Factory.ServersPath, doc.ToJsonString());

            Client = Factory.CreateClientWithToken(AgentTestFactory.AdminToken);
            Registry = Factory.Services.GetRequiredService<ServerRegistry>();
            Store = Factory.Services.GetRequiredService<ServerStore>();
            Backups = Factory.Services.GetRequiredService<BackupService>();
        }

        public AgentTestFactory Factory { get; }
        public HttpClient Client { get; }
        public ServerRegistry Registry { get; }
        public ServerStore Store { get; }
        public BackupService Backups { get; }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
