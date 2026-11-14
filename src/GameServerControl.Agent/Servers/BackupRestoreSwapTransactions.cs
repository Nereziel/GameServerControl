using Microsoft.Extensions.Logging;

namespace GameServerControl.Agent.Servers;

public sealed class RestoreSwapStep
{
    public required string Target { get; init; }
    public required string Stage { get; init; }
    public required string Old { get; init; }
    public bool HadOriginal { get; init; }
    public bool OriginalMoved { get; set; }
    public bool StageMoved { get; set; }
}

/// <summary>
/// Apply a prepared restore with a reversible phase separate from committed
/// original-data cleanup. On rollback failures never delete the last old copy.
/// </summary>
public static class BackupRestoreSwapTransactions
{
    public static void Commit(
        IReadOnlyList<RestoreSwapStep> swaps, CancellationToken ct = default,
        Action<string, string>? moveDirectory = null)
    {
        var move = moveDirectory ?? Directory.Move;
        foreach (var swap in swaps)
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(swap.Target)
                ?? throw new InvalidOperationException("Save directory has no parent."));
            if (swap.HadOriginal)
            {
                move(swap.Target, swap.Old);
                swap.OriginalMoved = true;
            }
            move(swap.Stage, swap.Target);
            swap.StageMoved = true;
        }
    }

    public static bool Rollback(IReadOnlyList<RestoreSwapStep> swaps, ILogger? logger = null)
    {
        var success = true;
        foreach (var swap in swaps.AsEnumerable().Reverse())
        {
            try
            {
                if (swap.OriginalMoved && Directory.Exists(swap.Old))
                {
                    if (swap.StageMoved && Directory.Exists(swap.Target))
                    {
                        // Retain newly restored files until the original is back.
                        if (Directory.Exists(swap.Stage))
                            throw new IOException("Rollback staging directory already exists.");
                        Directory.Move(swap.Target, swap.Stage);
                    }
                    if (!Directory.Exists(swap.Target))
                        Directory.Move(swap.Old, swap.Target);
                }
                else if (swap.StageMoved && !swap.HadOriginal && Directory.Exists(swap.Target))
                {
                    if (Directory.Exists(swap.Stage))
                        throw new IOException("Rollback staging directory already exists.");
                    Directory.Move(swap.Target, swap.Stage);
                }
                if (swap.OriginalMoved && Directory.Exists(swap.Old))
                    success = false;
            }
            catch (Exception ex)
            {
                success = false;
                logger?.LogError(ex, "Restore rollback could not recover original at {Path}; retained copy may remain at {Old}", swap.Target, swap.Old);
            }
        }
        return success;
    }

    public static bool CleanupCommitted(
        IReadOnlyList<RestoreSwapStep> swaps, ILogger? logger = null,
        Action<string>? deleteDirectory = null)
    {
        // This is a post-commit operation; cleanup errors MUST NOT rollback.
        var delete = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));
        var retained = false;
        foreach (var swap in swaps.Where(s => s.OriginalMoved))
        {
            try { if (Directory.Exists(swap.Old)) delete(swap.Old); }
            catch (Exception ex)
            {
                retained = true;
                logger?.LogWarning(ex, "Old save copy retained after restore at {Path}", swap.Old);
            }
        }
        return retained;
    }
}
