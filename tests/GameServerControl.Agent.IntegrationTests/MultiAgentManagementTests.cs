using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using GameServerControl.Agent.MultiAgent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class MultiAgentManagementTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5099")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://192.168.1.50:5099")]
    [InlineData("http://localhost:5099")]
    [InlineData("http://100.128.0.1:5099")]
    [InlineData("https://localhost:5100")]
    [InlineData("https://user:password@server.example.com")]
    [InlineData("https://server.example.com/api")]
    [InlineData("https://server.example.com/?token=123")]
    [InlineData("ftp://server.example.com")]
    public void Remote_origin_rejects_unsafe_schemes_and_addresses(string origin)
        => Assert.Throws<ArgumentException>(() => ManagedHostStore.ValidateOrigin(origin));

    [Theory]
    [InlineData("https://pc2.example.ts.net:5100")]
    [InlineData("http://100.64.0.1:5099")]
    [InlineData("http://100.127.255.254:5099")]
    public void Remote_origin_accepts_tls_or_tailscale_addresses(string origin)
        => ManagedHostStore.ValidateOrigin(origin);

    [Fact]
    public async Task ReadOnly_can_view_host_list_but_cannot_register_or_remove_hosts()
    {
        using var factory = new AgentTestFactory();
        using var readonlyClient = factory.CreateClientWithToken(factory.CreateReadOnlyToken());
        using var adminClient = factory.CreateClientWithToken(AgentTestFactory.AdminToken);

        using var view = await readonlyClient.GetAsync("/api/hosts");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        var text = await view.Content.ReadAsStringAsync();
        Assert.Contains("\"id\":\"local\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AdminToken", text, StringComparison.OrdinalIgnoreCase);

        var invalid = new ManagedHostInput("pc2", "Second computer",
            "http://127.0.0.1:5099", "admin-token-testing-00001", "readonly-token-testing-00002");
        using var denied = await readonlyClient.PostAsJsonAsync("/api/hosts", invalid);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var adminRejected = await adminClient.PostAsJsonAsync("/api/hosts", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, adminRejected.StatusCode);

        using var remove = await readonlyClient.DeleteAsync("/api/hosts/pc2");
        Assert.Equal(HttpStatusCode.Forbidden, remove.StatusCode);
    }

    [Fact]
    public async Task Dashboard_lists_local_servers_and_sanitizes_credentials()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(factory.CreateReadOnlyToken());
        using var response = await client.GetAsync("/api/hosts/overview");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var local = json.RootElement[0];
        Assert.Equal("local", local.GetProperty("id").GetString());
        Assert.True(local.GetProperty("isLocal").GetBoolean());
        Assert.Equal(2, local.GetProperty("servers").GetArrayLength());
        Assert.DoesNotContain("alpha-rcon-secret", body);
        Assert.DoesNotContain("alpha-webhook-secret", body);

        using var unknown = await client.GetAsync("/api/hosts/unknown/api/status");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var negotiated = await client.PostAsync("/api/hosts/unknown/hubs/status/negotiate", null);
        Assert.Equal(HttpStatusCode.NotFound, negotiated.StatusCode);
    }

    [Fact]
    public void Registered_tokens_are_protected_at_rest_and_never_in_list()
    {
        using var factory = new AgentTestFactory();
        _ = factory.CreateClient();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:DataDir"] = factory.TempRoot
            }).Build();
        var host = new ManagedHostStore(config, factory.Services.GetRequiredService<IHostEnvironment>());
        const string admin = "top-secret-admin-token-0123456789";
        const string readonlyToken = "top-secret-readonly-token-0123456789";
        var registration = new ManagedHostInput("secondary", "Secondary PC",
            "https://secondary.example.ts.net", admin, readonlyToken);

        Assert.Equal("secondary", host.Add(registration).Id);
        Assert.Contains(host.List(), x => x.Id == "secondary");
        var persisted = File.ReadAllText(Path.Combine(factory.TempRoot, "managed-hosts.json"));
        Assert.DoesNotContain(admin, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(readonlyToken, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(admin, JsonSerializer.Serialize(host.List()), StringComparison.Ordinal);

        var reopened = new ManagedHostStore(config, factory.Services.GetRequiredService<IHostEnvironment>());
        Assert.Single(reopened.List());
        Assert.True(reopened.Delete("secondary"));
        Assert.Empty(reopened.List());
        Assert.Throws<ArgumentException>(() => reopened.Add(registration with { Id = "local" }));
    }
    [Fact]
    public async Task Remote_host_proxy_uses_caller_role_and_never_returns_remote_tokens()
    {
        const string remoteAdmin = "test-remote-admin-0123456789";
        const string remoteReadonly = "test-remote-readonly-0123456789";
        using var handler = new FakeRemoteAgent(remoteAdmin, remoteReadonly);
        using var factory = new AgentTestFactory(managedHostHandler: handler);
        using var admin = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        using var viewer = factory.CreateClientWithToken(factory.CreateReadOnlyToken());

        var registration = new ManagedHostInput("pc2", "PC 2",
            "https://pc2.example.ts.net", remoteAdmin, remoteReadonly);
        using var added = await admin.PostAsJsonAsync("/api/hosts", registration);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var addedText = await added.Content.ReadAsStringAsync();
        Assert.DoesNotContain(remoteAdmin, addedText);
        Assert.DoesNotContain(remoteReadonly, addedText);
        Assert.DoesNotContain("pc2.example.ts.net", addedText);

        using var view = await viewer.GetAsync("/api/hosts/overview");
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        var overview = await view.Content.ReadAsStringAsync();
        using var summary = JsonDocument.Parse(overview);
        var remote = summary.RootElement.EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == "pc2");
        Assert.True(remote.GetProperty("online").GetBoolean());
        Assert.Equal("Remote Game", remote.GetProperty("servers")[0].GetProperty("name").GetString());
        Assert.Equal("Running", remote.GetProperty("servers")[0].GetProperty("processState").GetString());
        Assert.DoesNotContain(remoteAdmin, overview);
        Assert.DoesNotContain(remoteReadonly, overview);
        Assert.DoesNotContain("fake-rcon-password", overview);

        using var get = await viewer.GetAsync("/api/hosts/pc2/api/servers");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.DoesNotContain("fake-rcon-password", await get.Content.ReadAsStringAsync());

        using var denied = await viewer.PostAsync("/api/hosts/pc2/api/servers/remote-one/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var allowed = await admin.PostAsync("/api/hosts/pc2/api/servers/remote-one/start", null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        using var negotiate = await viewer.PostAsync("/api/hosts/pc2/hubs/status/negotiate", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);

        using var nested = await admin.GetAsync("/api/hosts/pc2/api/hosts");
        Assert.Equal(HttpStatusCode.NotFound, nested.StatusCode);

        Assert.Contains(handler.Calls, call => call.Path == "/api/servers/remote-one/start"
            && call.Token == remoteAdmin && call.Method == "POST");
        Assert.Contains(handler.Calls, call => call.Path == "/api/servers"
            && call.Token == remoteReadonly);
        Assert.Contains(handler.Calls, call => call.Path == "/hubs/status/negotiate"
            && call.Token == remoteReadonly);
        Assert.DoesNotContain(handler.Calls, call => call.Token == AgentTestFactory.AdminToken);
    }

    private sealed class FakeRemoteAgent : HttpMessageHandler
    {
        private readonly string _admin;
        private readonly string _readonly;
        public ConcurrentQueue<(string Path, string Token, string Method)> Calls { get; } = new();

        public FakeRemoteAgent(string admin, string readonlyToken)
        {
            _admin = admin;
            _readonly = readonlyToken;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var token = request.Headers.Authorization?.Parameter ?? "";
            var method = request.Method.Method;
            Calls.Enqueue((path, token, method));
            var valid = token == _admin || token == _readonly;
            if (!valid)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            object? data = path switch
            {
                "/api/auth/me" => new { role = token == _admin ? "Admin" : "ReadOnly" },
                "/api/servers" => token == _admin
                    ? new[] { new { id = "remote-one", name = "Remote Game", gameType = "SteamGeneric",
                        hostingMode = "BareMetal", rconPassword = "fake-rcon-password" } }
                    : new[] { new { id = "remote-one", name = "Remote Game", gameType = "SteamGeneric",
                        hostingMode = "BareMetal", rconPassword = "" } },
                "/api/status" => new[] { new { id = "remote-one", processState = "Running",
                    cpuPercent = 12.5, memoryMB = 256L, pidInGuest = 1234 } },
                "/api/servers/remote-one/start" when token == _admin => new { success = true },
                "/hubs/status/negotiate" => new { connectionToken = "fake-connection", connectionId = "fake" },
                _ => null
            };
            return Task.FromResult(new HttpResponseMessage(data is null ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = data is null ? null : JsonContent.Create(data)
            });
        }
    }

}
