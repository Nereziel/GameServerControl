using System.Text.Json.Nodes;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class BackupOffsiteRetentionTests
{
    [Fact]
    public async Task Unavailable_offsite_storage_still_prunes_local_complete_archives()
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

    [Fact]
    public async Task Failed_offsite_preupdate_does_not_accumulate_unbounded_safety_archives()
    {
        using var fixture = new BackupFixture(normalRetention: 1);
        var normalResult = await fixture.Backups.CreateAsync("alpha");
        Assert.False(normalResult.Success);
        var normalName = ExtractArchiveName(normalResult.Message);
        string? newestSafety = null;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await fixture.Backups.CreatePreUpdateAsync("alpha");
            Assert.False(result.Success);
            Assert.Contains("Local pre-update snapshot retained", result.Message, StringComparison.Ordinal);
            Assert.Contains("update was cancelled", result.Message, StringComparison.Ordinal);
            var safety = (await fixture.Backups.ListAsync("alpha"))
                .Where(x => x.Kind == BackupKind.SafetyArchive).ToArray();
            Assert.NotEmpty(safety);
            newestSafety = safety[0].CheckpointName;
            Assert.True(File.Exists(Path.Combine(fixture.LocalBackupDir, newestSafety)));
        }

        var snapshots = await fixture.Backups.ListAsync("alpha");
        Assert.Equal(3, snapshots.Count(x => x.Kind == BackupKind.SafetyArchive));
        Assert.Single(snapshots.Where(x => x.Kind == BackupKind.Archive));
        Assert.Contains(snapshots, x => x.CheckpointName == normalName && x.Restorable);
        Assert.Contains(snapshots, x => x.CheckpointName == newestSafety && x.Restorable);
        Assert.Equal(4, Directory.GetFiles(fixture.LocalBackupDir, "*.zip").Length);
        Assert.NotNull(fixture.Status.Get("alpha").LastBackupAt);
    }

    [Fact]
    public async Task Failed_offsite_prerestore_prunes_safety_archives_without_restoring_world()
    {
        using var fixture = new BackupFixture(normalRetention: 1);
        var created = await fixture.Backups.CreateAsync("alpha");
        Assert.False(created.Success);
        var backupName = ExtractArchiveName(created.Message);
        var originalWorld = File.ReadAllText(fixture.WorldFile);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var restore = await fixture.Backups.RestoreAsync("alpha", backupName);
            Assert.False(restore.Success);
            Assert.Contains("Local pre-restore safety snapshot retained", restore.Message, StringComparison.Ordinal);
            Assert.Contains("restore was not started", restore.Message, StringComparison.Ordinal);
            Assert.Equal(originalWorld, File.ReadAllText(fixture.WorldFile));
        }

        var snapshots = await fixture.Backups.ListAsync("alpha");
        Assert.Equal(3, snapshots.Count(x => x.Kind == BackupKind.SafetyArchive));
        Assert.Contains(snapshots, x => x.CheckpointName == backupName && x.Restorable);
        Assert.Equal(4, Directory.GetFiles(fixture.LocalBackupDir, "*.zip").Length);
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
