using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameServerControl.Agent.Notifications;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class OperationsDiagnosticsTests
{
    [Theory]
    [InlineData("http://127.0.0.1/api/webhooks/123/abcdef")]
    [InlineData("https://localhost/api/webhooks/123/abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("https://discord.com.evil.test/api/webhooks/123/abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("https://discord.com@localhost/api/webhooks/123/abcdefghijklmnopqrstuvwxyz12345")]
    [InlineData("https://discord.com/api/webhooks/123/abcdefghijklmnopqrstuvwxyz12345?wait=true")]
    [InlineData("https://discord.com/api/webhooks/123/short")]
    public void Rejects_non_discord_or_unsafe_webhook_urls(string url)
        => Assert.False(DiscordNotifier.IsValidWebhookUrl(url));

    [Fact]
    public void Accepts_official_https_webhook_url()
        => Assert.True(DiscordNotifier.IsValidWebhookUrl(
            "https://discord.com/api/webhooks/123456789/abcdefghijklmnopqrstuvwxyz123456789"));

    [Fact]
    public async Task Diagnostics_returns_safe_bounded_events_and_operation_state()
    {
        var tracker = new ServerDiagnostics();
        var discord = new DiscordNotifier(NullLogger<DiscordNotifier>.Instance);
        var secret = "secret-token-value-should-never-appear";
        for (var i = 0; i < 70; i++)
            tracker.Record("alpha", "Test", "Info", "Event " + i);

        var result = await tracker.TrackAsync("alpha", "Update",
            () => Task.FromResult(new ActionResult(false, secret, "test-corr")), null, discord);
        Assert.False(result.Success);
        var snapshot = tracker.Snapshot("alpha");
        Assert.Equal(60, snapshot.Events.Count);
        Assert.Equal("Failed", snapshot.Operations["Update"].State);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);

        tracker.Record("alpha", "UnexpectedStop", "Error", "Process exited unexpectedly.");
        tracker.Record("alpha", "RecoveryObserved", "Success", "Process running again.");
        tracker.Record("alpha", "ManualRestart", "Info", "Operator requested restart.");
        var after = tracker.Snapshot("alpha");
        Assert.NotNull(after.LastUnexpectedStopAt);
        Assert.NotNull(after.LastRecoveryObservedAt);
        Assert.NotNull(after.LastManualRestartAt);
        Assert.Empty(tracker.Snapshot("beta").Events);
    }

    [Fact]
    public async Task Notification_settings_are_admin_only_and_never_echo_webhook()
    {
        using var factory = new AgentTestFactory();
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var readOnly = factory.CreateClientWithToken(factory.CreateReadOnlyToken());
        const string url = "https://discord.com/api/webhooks/123456789/abcdefghijklmnopqrstuvwxyz123456789";

        using var initial = await readOnly.GetAsync("/api/servers/alpha/notifications");
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        Assert.DoesNotContain("webhook-secret", await initial.Content.ReadAsStringAsync());

        using var denied = await readOnly.PutAsJsonAsync("/api/servers/alpha/notifications",
            new { webhookUrl = url, alertsOnly = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var invalid = await admin.PutAsJsonAsync("/api/servers/alpha/notifications",
            new { webhookUrl = "https://127.0.0.1/private", alertsOnly = true });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var saved = await admin.PutAsJsonAsync("/api/servers/alpha/notifications",
            new { webhookUrl = url, alertsOnly = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBody = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(url, savedBody);

        using var latest = await admin.GetAsync("/api/servers/alpha/notifications");
        using var parsed = JsonDocument.Parse(await latest.Content.ReadAsStringAsync());
        Assert.True(parsed.RootElement.GetProperty("configured").GetBoolean());
        Assert.True(parsed.RootElement.GetProperty("alertsOnly").GetBoolean());

        var stored = File.ReadAllText(factory.ServersPath);
        Assert.DoesNotContain(url, stored, StringComparison.Ordinal);

        using var removed = await admin.PutAsJsonAsync("/api/servers/alpha/notifications",
            new { disable = true });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var after = await admin.GetAsync("/api/servers/alpha/notifications");
        using var afterJson = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.False(afterJson.RootElement.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Diagnostics_endpoint_requires_auth_and_never_exposes_secrets()
    {
        using var factory = new AgentTestFactory();
        using var anonymous = factory.CreateClient();
        using var readOnly = factory.CreateClientWithToken(factory.CreateReadOnlyToken());

        using var unauthorized = await anonymous.GetAsync("/api/servers/alpha/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var response = await readOnly.GetAsync("/api/servers/alpha/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("diagnostics", body);
        Assert.DoesNotContain("alpha-rcon-secret", body);
        Assert.DoesNotContain("alpha-webhook-secret", body);

        using var missing = await readOnly.GetAsync("/api/servers/nonexistent/diagnostics");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
