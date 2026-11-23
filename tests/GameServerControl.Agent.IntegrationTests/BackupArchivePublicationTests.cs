using System.IO.Compression;
using GameServerControl.Agent.Servers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class BackupArchivePublicationTests
{
    private const string CompleteMetadata = """{"Kind":"Archive","CreatedAt":"2026-11-23T10:00:00Z","FileCount":1,"SkippedFiles":0}""";

    [Fact]
    public async Task Cancelled_during_archive_write_leaves_no_published_zip_or_staging_file()
    {
        var root = NewRoot();
        var target = Path.Combine(root, "alpha-20261123-100000.zip");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BackupArchivePublication.PublishAsync(target,
                    async (stage, token) =>
                    {
                        await File.WriteAllTextAsync(stage, "partial archive bytes", token);
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                    },
                    _ => Task.CompletedTask, cancellation.Token));

            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancellation_after_metadata_write_removes_unpublished_sidecar()
    {
        var root = NewRoot();
        var target = Path.Combine(root, "alpha-20261123-100000.zip");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BackupArchivePublication.PublishAsync(target,
                    (stage, _) =>
                    {
                        WriteArchive(stage);
                        return Task.CompletedTask;
                    },
                    _ =>
                    {
                        File.WriteAllText(target + ".meta.json", CompleteMetadata);
                        cancellation.Cancel();
                        return Task.CompletedTask;
                    }, cancellation.Token));

            Assert.Empty(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Completed_archive_publishes_zip_only_after_sidecar_exists()
    {
        var root = NewRoot();
        var target = Path.Combine(root, "alpha-20261123-100000.zip");
        try
        {
            await BackupArchivePublication.PublishAsync(target,
                (stage, _) =>
                {
                    WriteArchive(stage);
                    Assert.False(File.Exists(target));
                    return Task.CompletedTask;
                },
                _ =>
                {
                    Assert.False(File.Exists(target));
                    File.WriteAllText(target + ".meta.json", CompleteMetadata);
                    return Task.CompletedTask;
                }, CancellationToken.None);

            Assert.True(File.Exists(target));
            Assert.Equal(CompleteMetadata, File.ReadAllText(target + ".meta.json"));
            Assert.Equal(2, Directory.GetFiles(root).Length);
            using var archive = ZipFile.OpenRead(target);
            Assert.Single(archive.Entries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Listing_rejects_archives_without_verified_complete_metadata()
    {
        using var baseFactory = new AgentTestFactory();
        var backupRoot = Path.Combine(baseFactory.TempRoot, "backups");
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agent:BackupRoot"] = backupRoot
                })));
        using var client = factory.CreateClient();
        var backupDir = Path.Combine(backupRoot, "alpha");
        Directory.CreateDirectory(backupDir);

        var complete = Path.Combine(backupDir, "alpha-complete.zip");
        var missing = Path.Combine(backupDir, "alpha-legacy.zip");
        var invalid = Path.Combine(backupDir, "alpha-bad-meta.zip");
        var truncated = Path.Combine(backupDir, "alpha-truncated-meta.zip");
        var partial = Path.Combine(backupDir, "alpha-skipped.zip");
        foreach (var file in new[] { complete, missing, invalid, truncated, partial })
            WriteArchive(file);
        File.WriteAllText(complete + ".meta.json", CompleteMetadata);
        File.WriteAllText(invalid + ".meta.json", "not json");
        File.WriteAllText(truncated + ".meta.json",
            """{"Kind":"Archive","CreatedAt":"2026-11-23T10:00:00Z","FileCount":1}""");
        File.WriteAllText(partial + ".meta.json",
            """{"Kind":"Archive","CreatedAt":"2026-11-23T10:00:00Z","FileCount":1,"SkippedFiles":1}""");

        var backups = await factory.Services.GetRequiredService<BackupService>().ListAsync("alpha");
        Assert.True(backups.Single(x => x.CheckpointName == Path.GetFileName(complete)).Restorable);
        Assert.False(backups.Single(x => x.CheckpointName == Path.GetFileName(missing)).Restorable);
        Assert.False(backups.Single(x => x.CheckpointName == Path.GetFileName(invalid)).Restorable);
        Assert.False(backups.Single(x => x.CheckpointName == Path.GetFileName(truncated)).Restorable);
        Assert.False(backups.Single(x => x.CheckpointName == Path.GetFileName(partial)).Restorable);

        var service = factory.Services.GetRequiredService<BackupService>();
        foreach (var archiveName in new[] { Path.GetFileName(missing), Path.GetFileName(invalid), Path.GetFileName(truncated), Path.GetFileName(partial) })
        {
            var restore = await service.RestoreAsync("alpha", archiveName);
            Assert.False(restore.Success);
            Assert.Contains("incomplete or unverified", restore.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-archive-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteArchive(string path)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("alpha-game/world.txt").Open());
        writer.Write("complete test save");
    }
}
