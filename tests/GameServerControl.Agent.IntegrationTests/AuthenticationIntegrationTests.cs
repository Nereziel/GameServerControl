using System.Net;
using Microsoft.Extensions.DependencyInjection;
using GameServerControl.Agent.Servers;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class AuthenticationIntegrationTests
{
    [Fact]
    public async Task Health_without_token_is_unauthorized()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_with_admin_bearer_returns_ok()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(AgentTestFactory.AdminToken);

        using var response = await client.GetAsync("/api/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ok\":true", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_query_string_token_is_rejected()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/health?access_token=" + Uri.EscapeDataString(AgentTestFactory.AdminToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnly_can_list_servers_without_plaintext_secrets()
    {
        using var factory = new AgentTestFactory();
        var token = factory.CreateReadOnlyToken();
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.GetAsync("/api/servers");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("alpha", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alpha-rcon-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("alpha-webhook-secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnly_cannot_reload_servers_but_admin_can()
    {
        using var factory = new AgentTestFactory();
        var token = factory.CreateReadOnlyToken();
        using var readOnly = factory.CreateClientWithToken(token);
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);

        using var denied = await readOnly.PostAsync("/api/servers/reload", content: null);
        using var allowed = await admin.PostAsync("/api/servers/reload", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(2, factory.Services.GetRequiredService<ServerRegistry>().All.Count);
    }
}
