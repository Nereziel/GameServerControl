using System.Security.Cryptography;
using System.Text;
using GameServerControl.Agent.Servers;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class OffsiteBackupMirrorTests
{
    private const string Metadata = """{"Kind":"Archive","CreatedAt":"2026-11-16T16:00:00Z","FileCount":1,"SkippedFiles":0}""";

    [Fact]
    public async Task Complete_backup_is_copied_with_matching_sha256_without_modifying_source()
    {
        var root = CreateFixture(out var archive, out var offsite);
        try
        {
            var source = File.ReadAllBytes(archive);
            var copied = await OffsiteBackupMirror.CopyAsync(archive, offsite, "minecraft-one", CancellationToken.None);
            var target = Path.Combine(offsite, "minecraft-one", Path.GetFileName(archive));
            Assert.Equal(target, copied);
            Assert.Equal(source, File.ReadAllBytes(target));
            Assert.Equal(Metadata, File.ReadAllText(target + ".meta.json"));
            Assert.Equal(SHA256.HashData(source), SHA256.HashData(File.ReadAllBytes(target)));
            Assert.Equal(source, File.ReadAllBytes(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Existing_corrupt_remote_backup_is_never_overwritten()
    {
        var root = CreateFixture(out var archive, out var offsite);
        try
        {
            var targetDir = Path.Combine(offsite, "minecraft-one");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, Path.GetFileName(archive));
            File.WriteAllText(target, "not the original archive");
            await Assert.ThrowsAsync<IOException>(() =>
                OffsiteBackupMirror.CopyAsync(archive, offsite, "minecraft-one", CancellationToken.None));
            Assert.Equal("not the original archive", File.ReadAllText(target));
            Assert.True(File.Exists(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Partial_backup_is_not_mirrored()
    {
        var root = CreateFixture(out var archive, out var offsite);
        try
        {
            File.WriteAllText(archive + ".meta.json", """{"Kind":"Archive","SkippedFiles":3}""");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                OffsiteBackupMirror.CopyAsync(archive, offsite, "minecraft-one", CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(offsite, "minecraft-one")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Missing_mount_marker_rejects_fallback_to_local_directory()
    {
        var root = CreateFixture(out var archive, out var offsite);
        try
        {
            File.Delete(Path.Combine(offsite, ".gsc-offsite-ready"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OffsiteBackupMirror.CopyAsync(archive, offsite, "minecraft-one", CancellationToken.None));
            Assert.False(Directory.Exists(Path.Combine(offsite, "minecraft-one")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Local_backup_root_cannot_be_reused_as_offsite_destination()
    {
        var root = CreateFixture(out var archive, out _);
        try
        {
            var localRoot = Path.GetDirectoryName(Path.GetDirectoryName(archive)!)!;
            await Assert.ThrowsAsync<ArgumentException>(() =>
                OffsiteBackupMirror.CopyAsync(archive, localRoot, "minecraft-one", CancellationToken.None));
            Assert.True(File.Exists(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Invalid_server_ids_cannot_escape_target_directory()
    {
        var root = CreateFixture(out var archive, out var offsite);
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                OffsiteBackupMirror.CopyAsync(archive, offsite, "../../elsewhere", CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Cancelled_copy_does_not_leave_published_backup()
    {
        var root = CreateFixture(out var archive, out var offsite);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                OffsiteBackupMirror.CopyAsync(archive, offsite, "minecraft-one", cts.Token));
            Assert.False(File.Exists(Path.Combine(offsite, "minecraft-one", Path.GetFileName(archive))));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateFixture(out string archive, out string offsite)
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-offsite-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "local", "minecraft-one");
        Directory.CreateDirectory(local);
        archive = Path.Combine(local, "minecraft-one-20261116.zip");
        File.WriteAllBytes(archive, Encoding.UTF8.GetBytes("zip world data for test"));
        File.WriteAllText(archive + ".meta.json", Metadata);
        offsite = Path.Combine(root, "mounted-remote");
        Directory.CreateDirectory(offsite); // Represents an already mounted external share.
        File.WriteAllText(Path.Combine(offsite, ".gsc-offsite-ready"), "GSC offsite share ready");
        return root;
    }
}
