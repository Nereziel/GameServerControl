using System.Net;
using GameServerControl.Agent.Servers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class ServerDeleteIntegrationTests
{
    [Fact]
    public async Task Delete_deprovisions_before_definition_removal_and_completes_afterward()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        factory.FakeRunner.ServerExists = id => registry.Get(id) is not null;

        using var response = await client.DeleteAsync("/api/servers/alpha");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, factory.FakeRunner.DeprovisionCalls);
        Assert.True(factory.FakeRunner.DeprovisionObservedServerPresent);
        Assert.Null(registry.Get("alpha"));
        Assert.Equal(1, factory.FakeRunner.CompleteCalls);
        Assert.False(factory.FakeRunner.CompleteObservedServerPresent);
    }

    [Fact]
    public async Task Delete_failure_keeps_definition_and_does_not_complete_deprovision()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        factory.FakeRunner.ServerExists = id => registry.Get(id) is not null;
        factory.FakeRunner.DeprovisionResult = (false, "blocked");

        using var response = await client.DeleteAsync("/api/servers/alpha");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("blocked", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.FakeRunner.DeprovisionCalls);
        Assert.True(factory.FakeRunner.DeprovisionObservedServerPresent);
        Assert.NotNull(registry.Get("alpha"));
        Assert.Equal(0, factory.FakeRunner.CompleteCalls);
    }
}
