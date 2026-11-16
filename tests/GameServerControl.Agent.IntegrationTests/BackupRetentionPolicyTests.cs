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
