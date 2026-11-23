using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Retain complete, incomplete, and safety archives independently.
/// A partial archive must never displace a restorable world snapshot.
/// </summary>
public static class BackupRetentionPolicy
{
    public const int PartialArchiveRetention = 3;
    public const int SafetyArchiveRetention = 3;

    public static IReadOnlyList<BackupInfo> SelectBareMetalEvictions(
        IEnumerable<BackupInfo> backups, int normalKeep)
    {
        if (normalKeep < 1) throw new ArgumentOutOfRangeException(nameof(normalKeep));
        var all = backups.ToArray();
        static IOrderedEnumerable<BackupInfo> NewestFirst(IEnumerable<BackupInfo> items) =>
            items.OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.CheckpointName, StringComparer.Ordinal);

        var oldComplete = NewestFirst(all.Where(x => x.Kind == BackupKind.Archive && x.Restorable
            && x.SkippedFiles is not > 0)).Skip(normalKeep);
        // Unverified historical archives (no trustworthy sidecar) may be the
        // only surviving save copy. Do not silently delete them as "partial".
        // Only positively identified skipped-file archives use the partial quota.
        var oldPartial = NewestFirst(all.Where(x => x.Kind == BackupKind.Archive
            && x.SkippedFiles is > 0)).Skip(PartialArchiveRetention);
        var oldSafety = NewestFirst(all.Where(x => x.Kind == BackupKind.SafetyArchive
            && x.Restorable && x.SkippedFiles is not > 0))
            .Skip(SafetyArchiveRetention);
        return oldComplete.Concat(oldPartial).Concat(oldSafety).ToArray();
    }
}
