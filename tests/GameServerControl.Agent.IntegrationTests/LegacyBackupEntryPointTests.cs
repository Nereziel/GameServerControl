using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class LegacyBackupEntryPointTests
{
    [Fact]
    public async Task Legacy_backup_list_restore_and_delete_share_the_safe_backup_service()
    {
        using var fixture = new Fixture();
        var orch = fixture.Orchestrator;
        var backups = fixture.Backups;

        var created = await orch.BackupAsync("alpha");
        Assert.True(created.Success, created.Message);

        var fromService = await backups.ListAsync("alpha");
        var first = Assert.Single(fromService);
        Assert.Equal(BackupKind.Archive, first.Kind);
        Assert.True(first.Restorable);
        Assert.True(first.FileCount > 0);
        Assert.True(File.Exists(fixture.ArchivePath(first.CheckpointName)));
        Assert.True(File.Exists(fixture.ArchivePath(first.CheckpointName) + ".meta.json"));

        var fromLegacy = await orch.ListBackupsAsync("alpha");
        Assert.Equal(fromService, fromLegacy);

        using (var httpList = await fixture.Http.GetAsync("/api/servers/alpha/backups"))
        {
            Assert.Equal(HttpStatusCode.OK, httpList.StatusCode);
            using var document = JsonDocument.Parse(await httpList.Content.ReadAsStringAsync());
            var serverBackup = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(first.CheckpointName, serverBackup.GetProperty("checkpointName").GetString());
            Assert.True(serverBackup.GetProperty("restorable").GetBoolean());
        }

        // The legacy restore must create a mandatory safety snapshot and use
        // the same staged swap as the BackupService rather than overwrite live
        // save files directly.
        File.WriteAllText(fixture.WorldPath, "changed after snapshot");
        var restored = await orch.RestoreBackupAsync("alpha", first.CheckpointName);
        Assert.True(restored.Success, restored.Message);
        Assert.Equal("original save", File.ReadAllText(fixture.WorldPath));
        Assert.Contains(await backups.ListAsync("alpha"), x => x.Kind == BackupKind.SafetyArchive);

        Assert.True(await orch.DeleteBackupAsync("alpha", first.CheckpointName));
        Assert.False(File.Exists(fixture.ArchivePath(first.CheckpointName)));
        Assert.False(File.Exists(fixture.ArchivePath(first.CheckpointName) + ".meta.json"));
        Assert.DoesNotContain(await backups.ListAsync("alpha"), x => x.CheckpointName == first.CheckpointName);
    }

    [Fact]
    public async Task Legacy_restore_refuses_zip_traversal_before_changing_any_save()
    {
        using var fixture = new Fixture();
        var name = "alpha-untrusted.zip";
        Directory.CreateDirectory(Path.Combine(fixture.BackupRoot, "alpha"));
        var archive = fixture.ArchivePath(name);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("alpha-game/../../unrelated.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("untrusted data");
        }

        var before = File.ReadAllText(fixture.WorldPath);
        var restored = await fixture.Orchestrator.RestoreBackupAsync("alpha", name);

        Assert.False(restored.Success);
        Assert.Equal(before, File.ReadAllText(fixture.WorldPath));
        Assert.Empty(Directory.GetDirectories(fixture.BackupRoot, "*", SearchOption.AllDirectories)
            .Where(x => x.Contains(".gsc-restore-", StringComparison.OrdinalIgnoreCase)));
        Assert.Empty((await fixture.Backups.ListAsync("alpha"))
            .Where(x => x.Kind == BackupKind.SafetyArchive));
    }

    [Fact]
    public async Task Legacy_delete_enforces_server_ownership_and_backup_filename_validation()
    {
        using var fixture = new Fixture();
        var created = await fixture.Orchestrator.BackupAsync("alpha");
        Assert.True(created.Success, created.Message);
        var original = Assert.Single(await fixture.Orchestrator.ListBackupsAsync("alpha"));

        Assert.False(await fixture.Orchestrator.DeleteBackupAsync("beta", original.CheckpointName));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Orchestrator.DeleteBackupAsync("alpha", "../alpha/" + original.CheckpointName));
        var invalidRestore = await fixture.Orchestrator.RestoreBackupAsync("alpha", "../alpha/" + original.CheckpointName);
        Assert.False(invalidRestore.Success);
        Assert.True(File.Exists(fixture.ArchivePath(original.CheckpointName)));
    }

    [Fact]
    public async Task Legacy_unknown_server_results_are_identical_to_backup_service()
    {
        using var fixture = new Fixture();
        var legacy = await fixture.Orchestrator.BackupAsync("missing");
        var current = await fixture.Backups.CreateAsync("missing");
        Assert.False(legacy.Success);
        Assert.Equal(current.Message, legacy.Message);
        Assert.False((await fixture.Orchestrator.RestoreBackupAsync("missing", "any.zip")).Success);
        Assert.False(await fixture.Orchestrator.DeleteBackupAsync("missing", "any.zip"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Orchestrator.ListBackupsAsync("missing"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly AgentTestFactory _parent;
        private readonly WebApplicationFactory<global::GameServerControl.Agent.AgentEntryPoint> _app;

        public Fixture()
        {
            _parent = new AgentTestFactory();
            var gameRoot = Path.Combine(_parent.TempRoot, "alpha-game");
            WorldPath = Path.Combine(gameRoot, "world.save");
            File.WriteAllText(WorldPath, "original save");

            // Configure an isolated backup directory. Never use default
            // production backup paths in these integration tests.
            BackupRoot = Path.Combine(_parent.TempRoot, "test-backups");
            var document = JsonNode.Parse(File.ReadAllText(_parent.ServersPath))!;
            var alpha = document["Servers"]![0]!;
            alpha["SaveDirs"] = new JsonArray(JsonValue.Create(gameRoot));
            var dummyExe = Path.Combine(gameRoot, "gsc-backup-test-" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllText(dummyExe, "not an executable");
            alpha["GuestExePath"] = dummyExe;
            File.WriteAllText(_parent.ServersPath, document.ToJsonString());

            _app = _parent.WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Agent:BackupRoot"] = BackupRoot,
                        ["Agent:OffsiteBackupRoot"] = ""
                    })));
            Http = _app.CreateClient();
            Http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", AgentTestFactory.AdminToken);
            Orchestrator = _app.Services.GetRequiredService<ServerOrchestrator>();
            Backups = _app.Services.GetRequiredService<BackupService>();
        }

        public HttpClient Http { get; }
        public string BackupRoot { get; }
        public string WorldPath { get; }
        public ServerOrchestrator Orchestrator { get; }
        public BackupService Backups { get; }

        public string ArchivePath(string name) => Path.Combine(BackupRoot, "alpha", name);

        public void Dispose()
        {
            Http.Dispose();
            _app.Dispose();
            _parent.Dispose();
        }
    }
}
