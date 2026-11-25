using System.Net;
using System.Net.Http.Json;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ApplicationLogicRegressionTests
{
    [Fact]
    public async Task Permanent_delete_is_admin_only_and_disabled_without_a_dedicated_root()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var reader = factory.CreateClientWithToken(factory.CreateReadOnlyToken());
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var original = registry.Get("alpha")!;
        var request = new DeleteServerRequest(original.Name, false);
        using var forbidden = await reader.PostAsJsonAsync("/api/servers/alpha/delete-completely", request);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        using var denied = await admin.PostAsJsonAsync("/api/servers/alpha/delete-completely", request);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("unsafe_path", await denied.Content.ReadAsStringAsync());
        Assert.True(File.Exists(original.GuestExePath));
        Assert.NotNull(registry.Get("alpha"));
    }

    [Fact]
    public async Task Both_delete_endpoints_and_definition_edit_reject_an_active_lifecycle_operation()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var gate = factory.Services.GetRequiredService<ServerLifecycleGate>();
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var def = registry.Get("alpha")!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = gate.RunExclusiveAsync("alpha", async () =>
        {
            entered.SetResult();
            await release.Task;
            return new ActionResult(true, "done", null);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var remove = await admin.DeleteAsync("/api/servers/alpha");
            using var permanent = await admin.PostAsJsonAsync("/api/servers/alpha/delete-completely", new DeleteServerRequest(def.Name, false));
            using var edit = await admin.PutAsJsonAsync("/api/servers/alpha", def with { Name = "New" });
            Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, permanent.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
            Assert.Equal(def.Name, registry.Get("alpha")!.Name);
            Assert.True(File.Exists(def.GuestExePath));
        }
        finally { release.TrySetResult(); await active; }
    }

    [Fact]
    public async Task Stale_definition_edit_cannot_restore_disabled_discord_webhook()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var stale = registry.Get("alpha")!;
        using var disable = await admin.PutAsJsonAsync("/api/servers/alpha/notifications", new { disable = true, alertsOnly = true });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        using var edit = await admin.PutAsJsonAsync("/api/servers/alpha", stale with { Name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var actual = registry.Get("alpha")!;
        Assert.Equal("Renamed", actual.Name);
        Assert.Null(actual.DiscordWebhookUrl);
        Assert.True(actual.DiscordAlertsOnly);
    }

    [Fact]
    public void Installation_preflight_rejects_duplicate_or_overlapping_registered_folder()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var store = factory.Services.GetRequiredService<ServerStore>();
        var def = registry.Get("alpha")! with { DiscordWebhookUrl = null, NetworkPorts = [] };
        Assert.Throws<InvalidOperationException>(() => store.ValidateNewInstallation(def, def.GuestWorkingDir));
        Assert.Throws<InvalidOperationException>(() => store.ValidateNewInstallation(def with { Id = "gamma" }, def.GuestWorkingDir));
    }
}
