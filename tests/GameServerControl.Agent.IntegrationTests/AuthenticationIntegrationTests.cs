using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Servers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    public async Task ReadOnly_trailing_slash_server_list_does_not_expose_plaintext_secrets()
    {
        using var factory = new AgentTestFactory();
        var token = factory.CreateReadOnlyToken();
        using var client = factory.CreateClientWithToken(token);

        using var response = await client.GetAsync("/api/servers/");
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

    [Fact]
    public async Task Valid_admin_token_authenticates_after_same_peer_is_throttled()
    {
        using var factory = new AgentTestFactory();
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:ApiToken"] = AgentTestFactory.AdminToken,
                ["Agent:DataDir"] = factory.TempRoot
            })
            .Build();
        var registry = new TokenRegistry(config, loggerFactory.CreateLogger<TokenRegistry>());
        var peer = IPAddress.Parse("192.0.2.123");

        for (var i = 0; i < 8; i++)
        {
            var rejected = await AuthenticateAsync(registry, loggerFactory, peer, "invalid-token-" + i);
            Assert.False(rejected.Succeeded);
        }

        var valid = await AuthenticateAsync(registry, loggerFactory, peer, AgentTestFactory.AdminToken);

        Assert.True(valid.Succeeded);
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        TokenRegistry registry,
        ILoggerFactory loggerFactory,
        IPAddress peer,
        string bearerToken)
    {
        var handler = new TokenAuthHandler(
            new FixedOptionsMonitor<TokenAuthOptions>(new TokenAuthOptions()),
            loggerFactory,
            UrlEncoder.Default,
            registry);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = peer;
        context.Request.Path = "/api/health";
        context.Request.Headers.Authorization = "Bearer " + bearerToken;
        var scheme = new AuthenticationScheme(
            TokenAuthHandler.SchemeName,
            TokenAuthHandler.SchemeName,
            typeof(TokenAuthHandler));

        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private sealed class FixedOptionsMonitor<TOptions>(TOptions value) : IOptionsMonitor<TOptions>
    {
        public TOptions CurrentValue => value;
        public TOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
