using GameServerControl.Relay;
using GameServerControl.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class RelayReconciliationEfficiencyTests
{
    [Fact]
    public async Task Unchanged_ruleset_skips_iptables_checks_but_drift_triggers_reconciliation()
    {
        if (!OperatingSystem.IsLinux()) return;

        var dir = Path.Combine(Path.GetTempPath(), "gsc-relay-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var iptables = Path.Combine(dir, "iptables");
            var iptablesSave = Path.Combine(dir, "iptables-save");
            var log = Path.Combine(dir, "calls.log");
            var ruleset = Path.Combine(dir, "ruleset.txt");

            File.WriteAllText(iptables, "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo 'iptables v1.8.9 (nf_tables)'; exit 0; fi\necho \"$*\" >> '" + log + "'\nexit 0\n");
            File.WriteAllText(iptablesSave,
                "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo 'iptables-save v1.8.9 (nf_tables)'; exit 0; fi\nprintf '# Generated at %s\\n' \"$(date +%s%N)\"\ncat '" + ruleset + "'\n");
            File.WriteAllText(ruleset,
                "*nat\n:GSC_RELAY_DNAT - [0:0]\n:GSC_RELAY_SNAT - [0:0]\nCOMMIT\n" +
                "*filter\n:GSC_RELAY_FWD - [0:0]\nCOMMIT\n");
            File.SetUnixFileMode(iptables,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(iptablesSave,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Relay:IptablesPath"] = iptables,
                ["Relay:IptablesSavePath"] = iptablesSave,
                ["Relay:PublicInterface"] = "ens18",
                ["Relay:TailscaleInterface"] = "tailscale0"
            }).Build();

            var relay = new IptablesRelay(config, NullLogger<IptablesRelay>.Instance);
            var routes = new[]
            {
                new RelayRouteDefinition("test", "100.100.10.1", [
                    new RelayRoutePort("Game", NetworkProtocol.Udp, 7777, null, 7777, null)
                ])
            };

            await relay.EnsureAsync(routes);
            var initialChecks = File.ReadAllLines(log).Length;
            Assert.True(initialChecks > 5);

            // Timestamp comments vary every snapshot but must not trigger repairs.
            await relay.EnsureAsync(routes);
            Assert.Equal(initialChecks, File.ReadAllLines(log).Length);

            // An out-of-band firewall change must force the original full check path.
            File.AppendAllText(ruleset, "-A GSC_RELAY_DNAT -j DROP\n");
            await relay.EnsureAsync(routes);
            var afterDrift = File.ReadAllLines(log).Length;
            Assert.True(afterDrift > initialChecks);
            Assert.Contains(File.ReadAllLines(log).Skip(initialChecks), line => line.Contains("-F GSC_RELAY_DNAT"));

            await relay.EnsureAsync(routes);
            Assert.Equal(afterDrift, File.ReadAllLines(log).Length);

            // API route changes invalidate the cached snapshot.
            await relay.RebuildAsync(routes);
            var afterMutation = File.ReadAllLines(log).Length;
            await relay.EnsureAsync(routes);
            Assert.True(File.ReadAllLines(log).Length > afterMutation);

            // Mismatched iptables backends must never enable snapshot caching.
            File.WriteAllText(iptablesSave,
                "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo 'iptables-save v1.8.9 (legacy)'; exit 0; fi\ncat '" + ruleset + "'\n");
            var mismatch = new IptablesRelay(config, NullLogger<IptablesRelay>.Instance);
            await mismatch.EnsureAsync(routes);
            var beforeMismatchedCheck = File.ReadAllLines(log).Length;
            await mismatch.EnsureAsync(routes);
            Assert.True(File.ReadAllLines(log).Length > beforeMismatchedCheck);

            // Without iptables-save, always fall back to the original full check.
            File.Delete(iptablesSave);
            var prior = File.ReadAllLines(log).Length;
            await relay.EnsureAsync(routes);
            Assert.True(File.ReadAllLines(log).Length > prior);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
