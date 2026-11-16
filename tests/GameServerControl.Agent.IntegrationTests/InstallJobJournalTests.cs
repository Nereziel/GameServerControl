using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Xunit;

namespace GameServerControl.Agent.IntegrationTests;

public sealed class InstallJobJournalTests
{
    [Fact]
    public void After_restart_unfinished_jobs_are_identified_as_interrupted()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-install-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new InstallJobJournal(Path.Combine(root, "install-jobs.json"));
            journal.Save(new[]
            {
                new InstallProgress("pending01", "download", "Downloading Minecraft", 40, false, false,
                    StartedAt: DateTimeOffset.Parse("2026-11-16T17:00:00+00:00"),
                    ServerName: "Minecraft Test", InstallPath: Path.Combine(root, "mc")),
                new InstallProgress("done01", "done", "Installed", 100, true, true,
                    StartedAt: DateTimeOffset.Parse("2026-11-16T16:00:00+00:00"))
            });
            var recovered = journal.Load();
            var interrupted = Assert.Single(recovered, x => x.JobId == "pending01");
            Assert.True(interrupted.Finished);
            Assert.False(interrupted.Success);
            Assert.Equal("interrupted", interrupted.Phase);
            Assert.Contains("restart", interrupted.Line, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Minecraft Test", interrupted.ServerName);
            var completed = Assert.Single(recovered, x => x.JobId == "done01");
            Assert.True(completed.Success);
            Assert.Equal("done", completed.Phase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Invalid_journal_entries_are_not_loaded()
    {
        var root = Path.Combine(Path.GetTempPath(), "gsc-install-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var journal = new InstallJobJournal(Path.Combine(root, "install-jobs.json"));
            journal.Save(new[]
            {
                new InstallProgress("", "queued", "invalid", 0, false, false),
                new InstallProgress("valid123", "done", "complete", 100, true, true)
            });
            Assert.Equal("valid123", Assert.Single(journal.Load()).JobId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
