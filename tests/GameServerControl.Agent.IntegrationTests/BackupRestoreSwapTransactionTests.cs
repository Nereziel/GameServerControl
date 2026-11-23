using GameServerControl.Agent.Servers;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class BackupRestoreSwapTransactionTests
{
    [Fact]
    public void Failure_during_second_swap_restores_both_originals()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plans = CreatePlans(root);
        try
        {
            Assert.Throws<IOException>(() =>
                BackupRestoreSwapTransactions.Commit(plans, moveDirectory: (from, to) =>
                {
                    if (from == plans[1].Stage) throw new IOException("Injected second-stage failure");
                    Directory.Move(from, to);
                }));
            Assert.True(BackupRestoreSwapTransactions.Rollback(plans));
            Assert.Equal("old-A", File.ReadAllText(Path.Combine(plans[0].Target, "save.txt")));
            Assert.Equal("old-B", File.ReadAllText(Path.Combine(plans[1].Target, "save.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Cancellation_before_second_swap_restores_all_originals()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plans = CreatePlans(root);
        using var cts = new CancellationTokenSource();
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                BackupRestoreSwapTransactions.Commit(plans, cts.Token, moveDirectory: (from, to) =>
                {
                    Directory.Move(from, to);
                    if (from == plans[0].Stage) cts.Cancel();
                }));
            Assert.True(BackupRestoreSwapTransactions.Rollback(plans));
            Assert.Equal("old-A", File.ReadAllText(Path.Combine(plans[0].Target, "save.txt")));
            Assert.Equal("old-B", File.ReadAllText(Path.Combine(plans[1].Target, "save.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Failure_during_cleanup_does_not_undo_successful_restore()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-restore-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var plans = CreatePlans(root);
        try
        {
            BackupRestoreSwapTransactions.Commit(plans);
            var retained = BackupRestoreSwapTransactions.CleanupCommitted(plans,
                deleteDirectory: path =>
                {
                    if (path == plans[1].Old) throw new IOException("Injected cleanup failure");
                    Directory.Delete(path, recursive: true);
                });
            Assert.True(retained);
            Assert.Equal("new-A", File.ReadAllText(Path.Combine(plans[0].Target, "save.txt")));
            Assert.Equal("new-B", File.ReadAllText(Path.Combine(plans[1].Target, "save.txt")));
            Assert.True(Directory.Exists(plans[1].Old));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static RestoreSwapStep[] CreatePlans(string root)
    {
        return new[] { "A", "B" }.Select(id =>
        {
            var target = Path.Combine(root, id);
            var stage = target + ".gsc-restore-test";
            var old = target + ".gsc-old-test";
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(target, "save.txt"), "old-" + id);
            File.WriteAllText(Path.Combine(stage, "save.txt"), "new-" + id);
            return new RestoreSwapStep { Target = target, Stage = stage, Old = old, HadOriginal = true };
        }).ToArray();
    }
}
