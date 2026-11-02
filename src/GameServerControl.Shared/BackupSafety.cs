namespace GameServerControl.Shared;

public static class BackupSafety
{
    public static string NormalizeArchiveEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("Backup archive entry path is empty.");

        var value = entryName.Replace('\\', '/');
        if (value.StartsWith("/", StringComparison.Ordinal)
            || (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':'))
            throw new InvalidDataException("Backup archive contains a rooted path.");

        var pieces = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0 || pieces.Any(x => x is "." or ".."))
            throw new InvalidDataException("Backup archive contains an unsafe relative path.");
        if (pieces.Any(x => x.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("Backup archive contains an invalid path segment.");

        return string.Join('/', pieces);
    }

    public static string ValidateBackupFileName(string backupName)
    {
        if (string.IsNullOrWhiteSpace(backupName))
            throw new ArgumentException("Backup name is required.", nameof(backupName));
        if (backupName.Length > 240
            || backupName.Contains('/')
            || backupName.Contains('\\')
            || backupName.Contains(':')
            || backupName.Any(char.IsControl)
            || !backupName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(backupName), backupName, StringComparison.Ordinal))
            throw new ArgumentException("Invalid backup archive name.", nameof(backupName));
        return backupName;
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Root path is required.", nameof(root));

        var relative = NormalizeArchiveEntry(relativePath);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(rootFull, relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Resolved backup path escapes its allowed root.");
        return resolved;
    }
}
