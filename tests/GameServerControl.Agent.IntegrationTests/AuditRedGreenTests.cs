using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ServerActionResult = GameServerControl.Shared.ActionResult;

namespace GameServerControl.Agent.IntegrationTests;

// These probes intentionally use interfaces present before and after the audit
// fixes. They must execute against both revisions, not fail to compile because
// a new helper is absent. All files, processes and credentials are test-only.
public sealed class AuditRedGreenSafetyTests
{
    [Fact]
    public async Task P1_1_Unverified_archive_is_not_restorable()
    {
        using var parent = new AgentTestFactory();
        var backupRoot = Path.Combine(parent.TempRoot, "audit-backups");
        using var factory = parent.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agent:BackupRoot"] = backupRoot,
                    ["Agent:OffsiteBackupRoot"] = ""
                })));
        using var client = factory.CreateClient();
        var dir = Path.Combine(backupRoot, "alpha");
        Directory.CreateDirectory(dir);
        const string name = "alpha-unverified.zip";
        using (var zip = ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("alpha-game/world.txt").Open()))
            writer.Write("isolated test save");

        var item = Assert.Single(await factory.Services.GetRequiredService<BackupService>().ListAsync("alpha"));
        Assert.Equal(name, item.CheckpointName);
        Assert.False(item.Restorable, "P1.1: a ZIP without verified metadata must not be offered for restore.");
    }

    [Fact]
    public async Task P1_2_Restore_rejects_an_active_same_server_lifecycle_operation()
    {
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClientWithToken(AgentTestFactory.AdminToken);
        var orch = factory.Services.GetRequiredService<ServerOrchestrator>();

        // This private adapter is present in both the old and fixed Orchestrator.
        // Hold the actual lifecycle lock without starting any real game process.
        var adapter = typeof(ServerOrchestrator).GetMethod("WithLifecycleLockAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(adapter);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Task<ServerActionResult>> fakeStart = async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
            return new ServerActionResult(true, "isolated test", "test");
        };
        var active = (Task<ServerActionResult>)adapter.Invoke(orch,
            new object[] { "alpha", fakeStart, CancellationToken.None })!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.PostAsync("/api/servers/alpha/backups/nonexistent.zip/restore", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(payload.RootElement.GetProperty("success").GetBoolean());
            var message = payload.RootElement.GetProperty("message").GetString();
            Assert.Contains("another", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Restore", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.TrySetResult(true);
            await active.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task P1_3_Linux_cleanup_preserves_other_instance_with_same_executable_name()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var factory = new AgentTestFactory();
        using var client = factory.CreateClient();
        var registry = factory.Services.GetRequiredService<ServerRegistry>();
        var local = factory.Services.GetRequiredService<LocalProcessService>();
        var alphaRoot = registry.Get("alpha")!.GuestWorkingDir;
        var betaRoot = registry.Get("beta")!.GuestWorkingDir;
        // Unique Linux comm (<= 15 characters): no unrelated real process
        // can be matched in the test runner's isolated environment.
        var name = "gsc" + Guid.NewGuid().ToString("N")[..10];
        var binary = File.Exists("/usr/bin/sleep") ? "/usr/bin/sleep" : "/bin/sleep";
        var aExe = Path.Combine(alphaRoot, name);
        var bExe = Path.Combine(betaRoot, name);
        File.Copy(binary, aExe);
        File.Copy(binary, bExe);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(aExe, mode);
        File.SetUnixFileMode(bExe, mode);
        using var a = Process.Start(new ProcessStartInfo(aExe, "30")
        {
            WorkingDirectory = alphaRoot, UseShellExecute = false
        })!;
        using var b = Process.Start(new ProcessStartInfo(bExe, "30")
        {
            WorkingDirectory = betaRoot, UseShellExecute = false
        })!;
        try
        {
            Assert.Equal(2, Process.GetProcessesByName(name).Count(p => p.Id == a.Id || p.Id == b.Id));
            await local.KillByNamesAsync(registry.Get("alpha")!, new[] { name });
            await a.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200);
            Assert.False(b.HasExited, "P1.3: stopping alpha extras must preserve beta's unrelated process.");
        }
        finally
        {
            try { if (!a.HasExited) a.Kill(entireProcessTree: true); } catch { }
            try { if (!b.HasExited) b.Kill(entireProcessTree: true); } catch { }
            try { await a.WaitForExitAsync(); } catch { }
            try { await b.WaitForExitAsync(); } catch { }
        }
    }
}

public sealed class AuditRedGreenOffsiteTests
{
    [Fact]
    public async Task P2_4_Unavailable_offsite_storage_still_prunes_local_complete_archives()
    {
        using var fixture = new BackupFixture(normalRetention: 2);
        string? latest = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await fixture.Backups.CreateAsync("alpha");
            Assert.False(result.Success);
            Assert.Contains("Local backup retained:", result.Message, StringComparison.Ordinal);
            Assert.Contains("Offsite backup failed", result.Message, StringComparison.Ordinal);
            latest = ExtractArchiveName(result.Message);
            Assert.True(File.Exists(Path.Combine(fixture.LocalBackupDir, latest)));
        }

        var snapshots = await fixture.Backups.ListAsync("alpha");
        var normals = snapshots.Where(x => x.Kind == BackupKind.Archive).ToArray();
        Assert.Equal(2, normals.Length);
        Assert.All(normals, item =>
        {
            Assert.True(item.Restorable);
            Assert.Equal(0, item.SkippedFiles);
            Assert.True(File.Exists(Path.Combine(fixture.LocalBackupDir, item.CheckpointName)));
            Assert.True(File.Exists(Path.Combine(fixture.LocalBackupDir, item.CheckpointName + ".meta.json")));
        });
        Assert.Contains(normals, item => item.CheckpointName == latest);
        Assert.Equal(2, Directory.GetFiles(fixture.LocalBackupDir, "*.zip").Length);
        Assert.NotNull(fixture.Status.Get("alpha").LastBackupAt);
        Assert.False(Directory.Exists(Path.Combine(fixture.OffsiteRoot, "alpha")));
    }

    private static string ExtractArchiveName(string message)
    {
        const string prefix = "Local backup retained: ";
        var start = message.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += prefix.Length;
        var end = message.IndexOf(".zip", start, StringComparison.Ordinal);
        Assert.True(end >= start);
        return message[start..(end + 4)];
    }

    private sealed class BackupFixture : IDisposable
    {
        private readonly AgentTestFactory _baseFactory;
        private readonly WebApplicationFactory<global::Program> _factory;

        public BackupFixture(int normalRetention)
        {
            _baseFactory = new AgentTestFactory();
            var alphaRoot = Path.Combine(_baseFactory.TempRoot, "alpha-game");
            WorldFile = Path.Combine(alphaRoot, "world.save");
            File.WriteAllText(WorldFile, "original save data");

            var document = JsonNode.Parse(File.ReadAllText(_baseFactory.ServersPath))!;
            document["Servers"]![0]!["SaveDirs"] = new JsonArray(JsonValue.Create(alphaRoot));
            File.WriteAllText(_baseFactory.ServersPath, document.ToJsonString());

            var localRoot = Path.Combine(_baseFactory.TempRoot, "local-backups");
            LocalBackupDir = Path.Combine(localRoot, "alpha");
            OffsiteRoot = Path.Combine(_baseFactory.TempRoot, "unavailable-offsite");
            Directory.CreateDirectory(OffsiteRoot);
            // Deliberately do not write .gsc-offsite-ready: this simulates a
            // missing/unmounted share, never a real external destination.
            _factory = _baseFactory.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Agent:BackupRoot"] = localRoot,
                        ["Agent:OffsiteBackupRoot"] = OffsiteRoot,
                        ["Agent:BackupRetention"] = normalRetention.ToString()
                    })));

            using var client = _factory.CreateClient();
            Backups = _factory.Services.GetRequiredService<BackupService>();
            Status = _factory.Services.GetRequiredService<StatusTracker>();
        }

        public BackupService Backups { get; }
        public StatusTracker Status { get; }
        public string LocalBackupDir { get; }
        public string OffsiteRoot { get; }
        public string WorldFile { get; }

        public void Dispose()
        {
            _factory.Dispose();
            _baseFactory.Dispose();
        }
    }
}


public sealed class AuditRedGreenSettingsTests
{
    [Fact]
    public void P2_6_Full_definition_edit_does_not_reset_dedicated_settings_from_stale_snapshot()
    {
        using var fixture = new Fixture(steam: true);
        var stale = fixture.Registry.Get("alpha")!;

        fixture.Backups.SaveSettings("alpha", 15);
        fixture.Store.SetAutoUpdatePreferences("alpha", onStart: true, onRestart: true);

        // The general editor received an older server snapshot and did not
        // edit any settings owned by their dedicated endpoints.
        var updated = fixture.Store.Update("alpha", stale with { Name = "Edited safely" });

        Assert.Equal("Edited safely", updated.Name);
        Assert.Equal(15, updated.BackupRetention);
        Assert.True(updated.AutoUpdateOnStart);
        Assert.True(updated.AutoUpdateOnRestart);
        Assert.Equal(15, fixture.Backups.GetSettings("alpha").Retention);
        AssertPersisted(fixture, "Edited safely", 15);
    }

    private static void AssertPersisted(Fixture fixture, string name, int retention)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(fixture.Factory.ServersPath));
        var stored = document.RootElement.GetProperty("Servers").EnumerateArray()
            .Single(x => x.GetProperty("Id").GetString() == "alpha");
        Assert.Equal(name, stored.GetProperty("Name").GetString());
        Assert.Equal(retention, stored.GetProperty("BackupRetention").GetInt32());
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(bool steam = false)
        {
            Factory = new AgentTestFactory(alphaUsesStdinProfile: steam);
            // The fixture's placeholder webhook is intentionally not a valid
            // Discord URL. The full-definition editor validates it, so remove
            // it here without changing the production validation contract.
            var doc = JsonNode.Parse(File.ReadAllText(Factory.ServersPath))!;
            doc["Servers"]![0]!["DiscordWebhookUrl"] = null;
            File.WriteAllText(Factory.ServersPath, doc.ToJsonString());

            Client = Factory.CreateClientWithToken(AgentTestFactory.AdminToken);
            Registry = Factory.Services.GetRequiredService<ServerRegistry>();
            Store = Factory.Services.GetRequiredService<ServerStore>();
            Backups = Factory.Services.GetRequiredService<BackupService>();
        }

        public AgentTestFactory Factory { get; }
        public HttpClient Client { get; }
        public ServerRegistry Registry { get; }
        public ServerStore Store { get; }
        public BackupService Backups { get; }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }
}
