namespace GameServerControl.Shared;

/// <summary>
/// Fail-closed ownership checks for additional processes named by StopProcessNames.
/// A matching executable leaf name alone is NEVER evidence of server ownership.
/// </summary>
public static class ProcessCleanupScope
{
    public readonly record struct ParentLink(int Pid, int ParentPid);

    public static IReadOnlySet<int> DescendantIds(int rootPid, IEnumerable<ParentLink> processTable)
    {
        var children = processTable.Where(x => x.Pid > 0 && x.Pid != x.ParentPid)
            .GroupBy(x => x.ParentPid)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Pid).ToArray());
        var descendants = new HashSet<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);

        while (queue.TryDequeue(out var parent))
        {
            if (!children.TryGetValue(parent, out var next)) continue;
            foreach (var child in next)
            {
                if (child == rootPid || !descendants.Add(child)) continue;
                queue.Enqueue(child);
            }
        }
        return descendants;
    }

    public static bool IsWithinWorkingDirectory(string? executable, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(workingDirectory)
            || !Path.IsPathFullyQualified(executable) || !Path.IsPathFullyQualified(workingDirectory))
            return false;
        try
        {
            var file = Path.GetFullPath(executable);
            var fullRoot = Path.GetFullPath(workingDirectory);
            // A filesystem/drive root is not a dedicated server installation.
            if (string.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetPathRoot(fullRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return false;
            var root = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return file.StartsWith(root + Path.DirectorySeparatorChar, comparison);
        }
        catch { return false; }
    }

    public static bool IsSameDirectory(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                comparison);
        }
        catch { return false; }
    }

    public static bool IsAllowedExtra(
        string? executable, string? workingDirectory, IEnumerable<string> configuredNames)
    {
        if (!IsWithinWorkingDirectory(executable, workingDirectory)) return false;
        var leaf = Path.GetFileNameWithoutExtension(executable);
        return !string.IsNullOrWhiteSpace(leaf) && configuredNames.Any(name =>
            string.Equals(Path.GetFileNameWithoutExtension(name), leaf, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsOwnedLinuxExtra(
        string? executable, string? processWorkingDirectory,
        string? serverWorkingDirectory, IEnumerable<string> configuredNames)
        => IsSameDirectory(processWorkingDirectory, serverWorkingDirectory)
            && IsAllowedExtra(executable, serverWorkingDirectory, configuredNames);
}
