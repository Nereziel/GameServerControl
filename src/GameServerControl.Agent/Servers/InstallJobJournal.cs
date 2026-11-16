using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Durable, ACL-protected installation job summaries. A process interrupted by
/// an Agent restart is reported as interrupted rather than silently disappearing.
/// Payloads, tokens and unbounded log output are deliberately never persisted.
/// </summary>
public sealed class InstallJobJournal
{
    private readonly string _path;
    public const int MaxRecords = 100;

    public InstallJobJournal(string path)
    {
        _path = Path.GetFullPath(path);
    }

    public IReadOnlyList<InstallProgress> Load()
    {
        if (!File.Exists(_path)) return Array.Empty<InstallProgress>();
        var entries = JsonSerializer.Deserialize<InstallProgress[]>(File.ReadAllText(_path))
            ?? Array.Empty<InstallProgress>();
        var now = DateTimeOffset.UtcNow;
        return entries
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.JobId) && p.JobId.Length <= 64)
            .OrderByDescending(p => p.UpdatedAt ?? p.StartedAt ?? DateTimeOffset.MinValue)
            .Take(MaxRecords)
            .Select(p => p.Finished ? p : p with
            {
                Finished = true, Success = false, Phase = "interrupted",
                Line = "Agent restarted before installation finished. Check the install folder before retrying.",
                UpdatedAt = now
            })
            .ToArray();
    }

    public void Save(IEnumerable<InstallProgress> records)
    {
        var snapshots = records
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.JobId) && p.JobId.Length <= 64)
            .OrderByDescending(p => p.UpdatedAt ?? p.StartedAt ?? DateTimeOffset.MinValue)
            .Take(MaxRecords)
            // Do not persist installer stdout, which may contain provider details.
            .Select(p => p with { LogLines = Array.Empty<string>() })
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        SecretFilePermissions.WriteAllTextAtomic(_path,
            JsonSerializer.Serialize(snapshots, new JsonSerializerOptions { WriteIndented = true }),
            _path + ".bak");
    }
}
