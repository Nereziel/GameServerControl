using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class BackupRetentionPolicyTests
{
    private static BackupInfo Snapshot(string name, int minute, BackupKind kind = BackupKind.Archive,
        bool restorable = true) => new(name, "example",
        new DateTimeOffset(2026, 11, 16, 18, 0, 0, TimeSpan.Zero).AddMinutes(minute),
        name + ".zip", 100, kind, SkippedFiles: restorable ? 0 : 1, Restorable: restorable);

    [Fact]
    public void Partial_archives_do_not_evict_the_last_complete_snapshots()
    {
        var archives = new[]
        {
            Snapshot("partial-new", 9, restorable: false),
            Snapshot("partial-2", 8, restorable: false),
            Snapshot("partial-3", 7, restorable: false),
            Snapshot("partial-old", 6, restorable: false),
            Snapshot("complete-new", 5),
            Snapshot("complete-2", 4),
            Snapshot("complete-old", 3)
        };
        var deleted = BackupRetentionPolicy.SelectBareMetalEvictions(archives, normalKeep: 2);
        Assert.Equal(new[] { "complete-old.zip", "partial-old.zip" },
            deleted.Select(item => item.CheckpointName).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(deleted, item => item.CheckpointName == "complete-2.zip");
    }

    [Fact]
    public void Unverified_legacy_archives_are_preserved_even_when_partial_quota_is_exceeded()
    {
        var archives = new[]
        {
            Snapshot("complete", 10),
            Snapshot("partial-4", 9, restorable: false),
            Snapshot("partial-3", 8, restorable: false),
            Snapshot("partial-2", 7, restorable: false),
            Snapshot("partial-1", 6, restorable: false),
            Snapshot("legacy-archive", 1) with { Restorable = false, SkippedFiles = null },
            Snapshot("corrupt-metadata", 0) with { Restorable = false, SkippedFiles = null },
            Snapshot("legacy-safety", 0, BackupKind.SafetyArchive) with
            {
                Restorable = false,
                SkippedFiles = null
            },
            Snapshot("safety-new-1", 10, BackupKind.SafetyArchive),
            Snapshot("safety-new-2", 9, BackupKind.SafetyArchive),
            Snapshot("safety-new-3", 8, BackupKind.SafetyArchive),
            Snapshot("safety-new-4", 7, BackupKind.SafetyArchive)
        };

        var evictions = BackupRetentionPolicy.SelectBareMetalEvictions(archives, normalKeep: 1);
        Assert.Contains(evictions, x => x.CheckpointName == "partial-1.zip");
        Assert.Contains(evictions, x => x.CheckpointName == "safety-new-4.zip");
        Assert.DoesNotContain(evictions, x => x.CheckpointName == "legacy-archive.zip");
        Assert.DoesNotContain(evictions, x => x.CheckpointName == "corrupt-metadata.zip");
        Assert.DoesNotContain(evictions, x => x.CheckpointName == "legacy-safety.zip");
    }

    [Fact]
    public void Safety_and_incomplete_archives_have_independent_quotas()
    {
        var archives = new[]
        {
            Snapshot("normal", 3),
            Snapshot("partial-a", 2, restorable: false),
            Snapshot("safety-1", 4, BackupKind.SafetyArchive),
            Snapshot("safety-2", 3, BackupKind.SafetyArchive),
            Snapshot("safety-3", 2, BackupKind.SafetyArchive),
            Snapshot("safety-4", 1, BackupKind.SafetyArchive)
        };
        var deleted = BackupRetentionPolicy.SelectBareMetalEvictions(archives, normalKeep: 1);
        Assert.Equal(new[] { "safety-4.zip" }, deleted.Select(item => item.CheckpointName));
    }
}
