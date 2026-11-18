using System.Net;
using GameServerControl.Agent.Auth;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class BrowserSessionTests
{
    [Fact]
    public void Remembered_browser_restores_after_restart_without_persisting_API_token()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var services = factory.Services;
        var registry = services.GetRequiredService<TokenRegistry>();
        var store = services.GetRequiredService<BrowserSessionStore>();
        var cookie = store.Remember("legacy", AgentTestFactory.AdminToken, registry);

        var stateFile = Path.Combine(factory.TempRoot, "remembered-browsers.json");
        var saved = File.ReadAllText(stateFile);
        Assert.DoesNotContain(cookie, saved, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentTestFactory.AdminToken, saved, StringComparison.Ordinal);
        Assert.Contains("CredentialHash", saved);

        var freshStore = new BrowserSessionStore(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>(),
            services.GetRequiredService<ILogger<BrowserSessionStore>>());
        var browserToken = freshStore.Restore(cookie, registry);
        Assert.NotNull(browserToken);
        Assert.Equal(TokenRole.Admin, freshStore.Lookup(browserToken, registry)?.Role);

        freshStore.Forget(cookie);
        Assert.Null(freshStore.Restore(cookie, registry));
        Assert.Null(freshStore.Lookup(browserToken, registry));
        Assert.DoesNotContain(cookie, File.ReadAllText(stateFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Revoking_credential_invalidates_browser_even_when_ID_is_reused()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var services = factory.Services;
        var registry = services.GetRequiredService<TokenRegistry>();
        var store = services.GetRequiredService<BrowserSessionStore>();
        var original = registry.Create("browser-admin", "Original", TokenRole.Admin);
        var cookie = store.Remember(original.Id, original.Token, registry);
        var access = store.Restore(cookie, registry);
        Assert.NotNull(access);

        Assert.True(registry.Delete(original.Id));
        var replacement = registry.Create("browser-admin", "Replacement", TokenRole.Admin);
        Assert.NotEqual(original.Token, replacement.Token);
        Assert.Null(store.Lookup(access, registry));
        Assert.Null(store.Restore(cookie, registry));
    }

    [Fact]
    public void Readonly_and_unmatched_bearers_cannot_be_remembered()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<TokenRegistry>();
        var store = factory.Services.GetRequiredService<BrowserSessionStore>();
        var readOnly = registry.Create("browser-readonly", "Read Only", TokenRole.ReadOnly);

        Assert.Throws<UnauthorizedAccessException>(() => store.Remember(readOnly.Id, readOnly.Token, registry));
        Assert.Throws<UnauthorizedAccessException>(() => store.Remember("legacy", "fake-token", registry));
        Assert.Throws<UnauthorizedAccessException>(() => store.Remember("legacy", readOnly.Token, registry));
    }

    [Fact]
    public void Browser_restore_requires_a_direct_same_origin_loopback_request()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost", 5099);
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.Equal(OperatingSystem.IsWindows(), BrowserSessionAccess.Allowed(ctx));
        ctx.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(BrowserSessionAccess.Allowed(ctx));
        ctx.Request.Headers.Remove("Sec-Fetch-Site");
        ctx.Request.Headers.Origin = "http://example.com";
        Assert.False(BrowserSessionAccess.Allowed(ctx));
        ctx.Request.Headers.Remove("Origin");
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("100.100.10.10");
        Assert.False(BrowserSessionAccess.Allowed(ctx));
        ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
        ctx.Request.Host = new HostString("example.com", 5099);
        Assert.False(BrowserSessionAccess.Allowed(ctx));
    }
}
