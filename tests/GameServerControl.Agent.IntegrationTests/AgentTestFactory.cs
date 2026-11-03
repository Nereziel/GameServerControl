using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Agent.Auth;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class AgentTestFactory : WebApplicationFactory<global::Program>
{
    public const string AdminToken = "integration-admin-token-0123456789abcdef";

    public AgentTestFactory(bool alphaUsesStdinProfile = false)
    {
        TempRoot = Path.Combine(Path.GetTempPath(), "gsc-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(Path.Combine(TempRoot, "alpha-game"));
        Directory.CreateDirectory(Path.Combine(TempRoot, "beta-game"));

        AlphaLogPath = Path.Combine(TempRoot, "alpha.log");
        BetaLogPath = Path.Combine(TempRoot, "beta.log");
        File.WriteAllText(AlphaLogPath, "");
        File.WriteAllText(BetaLogPath, "");

        ServersPath = Path.Combine(TempRoot, "servers.json");
        var doc = new
        {
            Servers = new[]
            {
                MakeServer(
                    "alpha",
                    "Alpha",
                    Path.Combine(TempRoot, "alpha-game"),
                    AlphaLogPath,
                    "alpha-rcon-secret",
                    "https://hooks.invalid/alpha-webhook-secret",
                    alphaUsesStdinProfile ? "105600" : "",
                    alphaUsesStdinProfile ? GameType.SteamGeneric : GameType.Custom),
                MakeServer(
                    "beta",
                    "Beta",
                    Path.Combine(TempRoot, "beta-game"),
                    BetaLogPath,
                    "beta-rcon-secret",
                    "https://hooks.invalid/beta-webhook-secret")
            }
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(ServersPath, json);
    }

    public string TempRoot { get; }
    public string ServersPath { get; }
    public string AlphaLogPath { get; }
    public string BetaLogPath { get; }
    public FakeWindowsRunnerServiceManager FakeRunner { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:ApiToken"] = AdminToken,
                ["Agent:DataDir"] = TempRoot,
                ["Agent:ServersJsonPath"] = ServersPath,
                ["Agent:AuditLogPath"] = Path.Combine(TempRoot, "audit"),
                ["Agent:PublicRelay:Url"] = "",
                ["Agent:PublicRelay:ApiToken"] = ""
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IWindowsRunnerServiceManager>();
            services.AddSingleton(FakeRunner);
            services.AddSingleton<IWindowsRunnerServiceManager>(sp => sp.GetRequiredService<FakeWindowsRunnerServiceManager>());
        });
    }

    public HttpClient CreateClientWithToken(string token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public string CreateReadOnlyToken()
    {
        _ = CreateClient();
        using var scope = Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenRegistry>();
        return tokens.Create("readonly-" + Guid.NewGuid().ToString("N"), "Integration ReadOnly", TokenRole.ReadOnly).Token;
    }

    public HubConnection CreateHubConnection(string token)
    {
        _ = CreateClient();
        return new HubConnectionBuilder()
            .WithUrl(new Uri("http://localhost/hubs/status"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            })
            .Build();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try { Directory.Delete(TempRoot, recursive: true); }
        catch { }
    }

    private static ServerDef MakeServer(
        string id,
        string name,
        string workingDirectory,
        string logPath,
        string rconPassword,
        string webhook,
        string steamAppId = "",
        GameType gameType = GameType.Custom)
    {
        var executable = Path.Combine(workingDirectory, "server.bin");
        File.WriteAllText(executable, "integration test placeholder");
        return new ServerDef(
            id,
            name,
            steamAppId,
            gameType,
            executable,
            workingDirectory,
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            null,
            logPath,
            "127.0.0.1",
            25575,
            rconPassword,
            HostingMode.BareMetal,
            null,
            null,
            webhook,
            Array.Empty<NetworkPort>(),
            null);
    }
}
