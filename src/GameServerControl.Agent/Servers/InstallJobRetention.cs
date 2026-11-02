using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public static class InstallJobRetention
{
    public static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);
    public const int MaxCompletedJobs = 100;

    public static IReadOnlyList<string> SelectEvictions(
        IEnumerable<InstallProgress> snapshots,
        DateTimeOffset now)
    {
        var cutoff = now - CompletedTtl;
        var completed = snapshots
            .Where(snapshot => snapshot.Finished)
            .Select(snapshot => new Candidate(
                snapshot.JobId,
                snapshot.UpdatedAt ?? snapshot.StartedAt ?? DateTimeOffset.MinValue))
            .ToArray();

        var evictions = completed
            .Where(candidate => candidate.UpdatedAt <= cutoff)
            .Select(candidate => candidate.JobId)
            .ToHashSet(StringComparer.Ordinal);

        var retained = completed
            .Where(candidate => !evictions.Contains(candidate.JobId))
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .ThenBy(candidate => candidate.JobId, StringComparer.Ordinal)
            .ToArray();

        foreach (var candidate in retained.Skip(MaxCompletedJobs))
            evictions.Add(candidate.JobId);

        return evictions.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }

    private sealed record Candidate(string JobId, DateTimeOffset UpdatedAt);
}
