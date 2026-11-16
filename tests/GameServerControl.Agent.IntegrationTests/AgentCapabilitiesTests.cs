using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class AgentCapabilitiesTests
{
    [Fact]
    public async Task Capabilities_are_visible_to_readonly_without_secrets()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(factory.CreateReadOnlyToken());
        using var response = await client.GetAsync("/api/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonDocument>();
        Assert.NotNull(result);
        Assert.True(result.RootElement.GetProperty("schemaVersion").GetInt32() >= 1);
        var capabilities = result.RootElement.GetProperty("features").EnumerateArray()
            .Select(x => x.GetString()).ToArray();
        Assert.Contains("minecraft.install", capabilities);
        Assert.Contains("install.cancel", capabilities);
        var raw = result.RootElement.GetRawText();
        Assert.DoesNotContain("ApiToken", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unknown_cancel_job_returns_not_found_to_admin()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var response = await client.PostAsync("/api/servers/install/unknown/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Cancel_endpoint_is_not_available_to_readonly()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(factory.CreateReadOnlyToken());
        using var response = await client.PostAsync("/api/servers/install/unknown/cancel", null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
