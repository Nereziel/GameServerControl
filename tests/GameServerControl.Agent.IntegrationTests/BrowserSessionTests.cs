using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    private static string PublicKey(ECDsa key) =>
        Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    private static string Sign(ECDsa key, string id, string challenge) =>
        Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes("gsc-browser-v1:" + id + ":" + challenge),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    [Fact]
    public void Browser_key_restores_after_restart_without_storing_private_or_API_credentials()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var services = factory.Services;
        var registry = services.GetRequiredService<TokenRegistry>();
        var store = services.GetRequiredService<BrowserSessionStore>();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = store.Register("legacy", AgentTestFactory.AdminToken, PublicKey(key), registry);

        var stateFile = Path.Combine(factory.TempRoot, "remembered-browsers.json");
        var saved = File.ReadAllText(stateFile);
        Assert.DoesNotContain(AgentTestFactory.AdminToken, saved, StringComparison.Ordinal);
        Assert.Contains("PublicKey", saved);

        var restarted = new BrowserSessionStore(
            services.GetRequiredService<IConfiguration>(),
            services.GetRequiredService<IHostEnvironment>(),
            services.GetRequiredService<ILogger<BrowserSessionStore>>());
        var challenge = restarted.CreateChallenge(id, registry);
        Assert.NotNull(challenge);
        var bearer = restarted.Redeem(id, challenge, Sign(key, id, challenge!), registry);
        Assert.NotNull(bearer);
        Assert.Equal(TokenRole.Admin, restarted.Lookup(bearer, registry)?.Role);

        // The proof cannot be replayed. A stolen public key does not sign challenges.
        Assert.Null(restarted.Redeem(id, challenge, Sign(key, id, challenge!), registry));
        var nextChallenge = restarted.CreateChallenge(id, registry);
        Assert.NotNull(nextChallenge);
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(restarted.Redeem(id, nextChallenge, Sign(wrongKey, id, nextChallenge!), registry));

        restarted.Forget(id);
        Assert.Null(restarted.CreateChallenge(id, registry));
        Assert.Null(restarted.Lookup(bearer, registry));
        Assert.DoesNotContain(id, File.ReadAllText(stateFile), StringComparison.Ordinal);
    }

    [Fact]
    public void Revoked_credentials_cannot_reauthorize_even_if_the_token_ID_is_recreated()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<TokenRegistry>();
        var store = factory.Services.GetRequiredService<BrowserSessionStore>();
        var admin = registry.Create("browser-admin", "Original", TokenRole.Admin);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = store.Register(admin.Id, admin.Token, PublicKey(key), registry);
        var challenge = store.CreateChallenge(id, registry);
        var access = store.Redeem(id, challenge, Sign(key, id, challenge!), registry);
        Assert.NotNull(access);

        Assert.True(registry.Delete(admin.Id));
        _ = registry.Create("browser-admin", "New Admin", TokenRole.Admin);
        Assert.Null(store.Lookup(access, registry));
        Assert.Null(store.CreateChallenge(id, registry));
    }

    [Fact]
    public void Readonly_token_invalid_public_key_and_unmatched_credentials_are_denied()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<TokenRegistry>();
        var store = factory.Services.GetRequiredService<BrowserSessionStore>();
        var viewer = registry.Create("viewer", "Read Only", TokenRole.ReadOnly);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Register(viewer.Id, viewer.Token, PublicKey(key), registry));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Register("legacy", "incorrect-token", PublicKey(key), registry));
        Assert.Throws<FormatException>(() =>
            store.Register("legacy", AgentTestFactory.AdminToken, "not-base64!", registry));
    }

    [Fact]
    public void Browser_access_requires_a_direct_same_origin_loopback_request()
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
