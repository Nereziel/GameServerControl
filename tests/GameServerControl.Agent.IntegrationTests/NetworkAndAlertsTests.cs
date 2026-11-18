using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameServerControl.Agent.Networking;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class NetworkAndAlertsTests
{
    private static ServerDef Definition(HostingMode hosting = HostingMode.BareMetal) => new(
        Id: "sample", Name: "Sample", VmName: "vm", GameType: GameType.Custom,
        GuestExePath: "server.exe", GuestWorkingDir: ".", StartArgs: [],
        SaveDirs: [], SteamAppId: null, GuestCredentialId: null, LogPathInGuest: null,
        HostingMode: hosting,
        NetworkPorts:
        [
            new NetworkPort("Game", NetworkProtocol.Udp, 7777),
            new NetworkPort("Beacon", NetworkProtocol.Udp, 8888),
            new NetworkPort("Admin", NetworkProtocol.Tcp, 9999)
        ]);

    [Fact]
    public void Local_socket_check_distinguishes_wildcard_loopback_and_missing_listener()
    {
        var result = ServerNetworkDiagnostics.Inspect(Definition(),
            [new IPEndPoint(IPAddress.Loopback, 1234)],
            [new IPEndPoint(IPAddress.Any, 7777), new IPEndPoint(IPAddress.Loopback, 8888)]);
        Assert.True(result.Supported);
        Assert.Equal("ListeningAny", result.Ports.Single(p => p.Port == 7777).State);
        Assert.Equal("LoopbackOnly", result.Ports.Single(p => p.Port == 8888).State);
        Assert.Equal("NotListening", result.Ports.Single(p => p.Port == 9999).State);
        Assert.Contains("not verified", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hyperv_guest_port_checks_are_not_claimed_to_work_on_host()
    {
        var result = ServerNetworkDiagnostics.Inspect(Definition(HostingMode.Vm), [], []);
        Assert.False(result.Supported);
        Assert.Empty(result.Ports);
    }

    [Fact]
    public void Relay_alert_requires_two_failures_and_resets_after_recovery()
    {
        var policy = new ServerAlertPolicy();
        Assert.False(policy.ReportRelay("alpha", true, false));
        Assert.True(policy.ReportRelay("alpha", true, false));
        Assert.False(policy.ReportRelay("alpha", true, false));
        Assert.False(policy.ReportRelay("alpha", true, true));
        Assert.False(policy.ReportRelay("alpha", true, false));
        Assert.True(policy.ReportRelay("alpha", true, false));
        Assert.False(policy.ReportRelay("alpha", false, false));
    }

    [Fact]
    public void Disk_alert_has_hysteresis_and_does_not_repeat()
    {
        var policy = new ServerAlertPolicy();
        Assert.False(policy.ReportFreeDisk("alpha", null));
        Assert.True(policy.ReportFreeDisk("alpha", ServerAlertPolicy.LowDiskBytes));
        Assert.False(policy.ReportFreeDisk("alpha", 100));
        Assert.False(policy.ReportFreeDisk("alpha", ServerAlertPolicy.LowDiskBytes + 1));
        Assert.False(policy.ReportFreeDisk("alpha", ServerAlertPolicy.DiskRecoveryBytes));
        Assert.True(policy.ReportFreeDisk("alpha", 0));
        Assert.False(policy.ReportFreeDisk("beta", ServerAlertPolicy.DiskRecoveryBytes));
    }

    [Fact]
    public void Diagnostic_events_survive_restart_but_secrets_and_operation_state_do_not()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gsc-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = new ServerDiagnostics(dir);
            for (var i = 0; i < 70; i++)
                original.Record("alpha", "UnexpectedStop", "Error", "sensitive-token-" + i);
            original.Record("alpha", "PrivateUnapprovedEvent", "Error", "secret-in-unsupported-event");
            var restored = new ServerDiagnostics(dir).Snapshot("alpha");

            Assert.Equal(60, restored.Events.Count);
            Assert.All(restored.Events, e => Assert.Equal("Server process exited unexpectedly.", e.Message));
            Assert.NotNull(restored.LastUnexpectedStopAt);
            Assert.Empty(restored.Operations);
            var json = File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "*.json")));
            Assert.DoesNotContain("sensitive-token", json, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-in-unsupported-event", json, StringComparison.Ordinal);
            Assert.Empty(new ServerDiagnostics(dir).Snapshot("beta").Events);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Network_diagnostics_endpoint_is_authenticated_and_readonly_accessible()
    {
        using var factory = new AgentTestFactory();
        using var anonymous = factory.CreateClient();
        using var readOnly = factory.CreateClientWithToken(factory.CreateReadOnlyToken());

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/servers/alpha/network/diagnostics")).StatusCode);
        using var response = await readOnly.GetAsync("/api/servers/alpha/network/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(data.RootElement.TryGetProperty("sockets", out _));
        Assert.True(data.RootElement.TryGetProperty("relay", out _));
        Assert.DoesNotContain("alpha-rcon-secret", data.RootElement.ToString());
        Assert.DoesNotContain("alpha-webhook-secret", data.RootElement.ToString());
        Assert.Equal(HttpStatusCode.NotFound,
            (await readOnly.GetAsync("/api/servers/not-found/network/diagnostics")).StatusCode);
    }
}
