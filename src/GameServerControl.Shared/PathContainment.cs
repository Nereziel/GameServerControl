namespace GameServerControl.Shared;

public static class PathContainment
{
    public static string ResolveDirectChild(string root, string childName)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root path is required.", nameof(root));
        if (string.IsNullOrWhiteSpace(childName)) throw new ArgumentException("Child name is required.", nameof(childName));
        if (childName is "." or "..") throw new ArgumentException("Relative path segments are not allowed.", nameof(childName));
        if (childName.Contains('/') || childName.Contains('\\'))
            throw new ArgumentException("Path separators are not allowed in a direct child name.", nameof(childName));
        if (Path.IsPathRooted(childName) || !string.Equals(Path.GetFileName(childName), childName, StringComparison.Ordinal))
            throw new ArgumentException("Child name must be a single file-system name.", nameof(childName));

        var canonicalRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, childName));
        if (!IsWithinRoot(canonicalRoot, candidate))
            throw new ArgumentException("Resolved path escapes the allowed root.", nameof(childName));
        return candidate;
    }

    public static bool IsWithinRoot(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;

        var canonicalRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var canonicalCandidate = Path.GetFullPath(candidate);
        var prefix = canonicalRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return canonicalCandidate.StartsWith(prefix, comparison);
    }
}
