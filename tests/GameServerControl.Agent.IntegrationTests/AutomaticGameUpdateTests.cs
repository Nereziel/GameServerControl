using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class AutomaticGameUpdateTests
{
    [Fact]
    public async Task Update_preferences_default_disabled_and_admin_can_toggle_without_leaking_secrets()
    {
        using var factory = new AgentTestFactory(alphaUsesLogProfile: true);
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var readOnly = factory.CreateClientWithToken(factory.CreateReadOnlyToken());

        using var initial = await readOnly.GetAsync("/api/servers/alpha/auto-update");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using (var payload = JsonDocument.Parse(await initial.Content.ReadAsStringAsync()))
        {
            Assert.False(payload.RootElement.GetProperty("autoUpdateOnStart").GetBoolean());
            Assert.False(payload.RootElement.GetProperty("autoUpdateOnRestart").GetBoolean());
            Assert.True(payload.RootElement.GetProperty("available").GetBoolean());
        }

        using var denied = await readOnly.PutAsJsonAsync("/api/servers/alpha/auto-update",
            new { autoUpdateOnStart = true, autoUpdateOnRestart = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var accepted = await admin.PutAsJsonAsync("/api/servers/alpha/auto-update",
            new { autoUpdateOnStart = true, autoUpdateOnRestart = false });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        registry.Reload();
        var def = registry.Get("alpha")!;
        Assert.True(def.AutoUpdateOnStart);
        Assert.False(def.AutoUpdateOnRestart);
        Assert.True(ServerOrchestrator.ShouldAutoUpdate(def, restart: false));
        Assert.False(ServerOrchestrator.ShouldAutoUpdate(def, restart: true));

        var stored = File.ReadAllText(factory.ServersPath);
        Assert.DoesNotContain("alpha-rcon-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-webhook-secret", stored, StringComparison.Ordinal);

        using var toggled = await admin.PutAsJsonAsync("/api/servers/alpha/auto-update",
            new { autoUpdateOnStart = false, autoUpdateOnRestart = true });
        Assert.Equal(HttpStatusCode.OK, toggled.StatusCode);
        def = registry.Get("alpha")!;
        Assert.False(ServerOrchestrator.ShouldAutoUpdate(def, restart: false));
        Assert.True(ServerOrchestrator.ShouldAutoUpdate(def, restart: true));
    }

    [Fact]
    public async Task Servers_without_steam_app_cannot_enable_automatic_updates()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var readOnly = factory.CreateClientWithToken(factory.CreateReadOnlyToken());

        using var invalid = await admin.PutAsJsonAsync("/api/servers/beta/auto-update",
            new { autoUpdateOnStart = true, autoUpdateOnRestart = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var missing = await admin.GetAsync("/api/servers/not-found/auto-update");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var deniedConsole = await readOnly.GetAsync("/api/servers/alpha/update/console");
        Assert.Equal(HttpStatusCode.Forbidden, deniedConsole.StatusCode);
        using var allowedConsole = await admin.GetAsync("/api/servers/alpha/update/console");
        Assert.Equal(HttpStatusCode.OK, allowedConsole.StatusCode);
        var text = await allowedConsole.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rcon-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Steamcmd_console_is_bounded_and_supports_incremental_reading()
    {
        var stream = new ServerUpdateConsole();
        stream.Begin("one", "Starting update");
        for (var n = 0; n < 270; n++)
            stream.Write("one", "Progress line " + n, n % 101);
        var all = stream.Snapshot("one");
        Assert.Equal("Running", all.Status);
        Assert.Equal(240, all.Lines.Count);
        Assert.Equal(271, all.LatestSequence);

        var next = stream.Snapshot("one", since: all.LatestSequence - 3);
        Assert.Equal(3, next.Lines.Count);
        stream.End("one", false, "SteamCMD exit 8");
        var failure = stream.Snapshot("one", since: all.LatestSequence);
        Assert.Equal("Failed", failure.Status);
        Assert.Single(failure.Lines);
        Assert.Contains("exit 8", failure.Lines[0].Text, StringComparison.OrdinalIgnoreCase);

        stream.Begin("one", "Retry");
        Assert.Equal("Running", stream.Snapshot("one").Status);
        Assert.Single(stream.Snapshot("one").Lines);
        Assert.Equal("Idle", stream.Snapshot("two").Status);
    }
}
