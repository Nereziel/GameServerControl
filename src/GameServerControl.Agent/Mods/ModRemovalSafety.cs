using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Validate sidecar paths and module directories before deleting files.
/// In addition to lexical containment, reject symlink/junction redirection.
/// </summary>
public static class ModRemovalSafety
{
    public static bool TryResolveInstalledFile(
        string serverRoot, string allowedModRoot, string relativeFile, out string absoluteFile)
    {
        absoluteFile = "";
        if (string.IsNullOrWhiteSpace(relativeFile) || Path.IsPathRooted(relativeFile)) return false;
        try
        {
            var root = Path.GetFullPath(serverRoot);
            var allowed = Path.GetFullPath(allowedModRoot);
            if (!PathContainment.IsWithinRoot(root, allowed)) return false;
            var candidate = Path.GetFullPath(Path.Combine(root, relativeFile));
            if (!PathContainment.IsWithinRoot(allowed, candidate) || HasReparseComponent(candidate))
                return false;
            absoluteFile = candidate;
            return true;
        }
        catch (Exception ex) when (IsPathException(ex)) { return false; }
    }

    public static bool TryResolveModDirectory(string modsRoot, string modId, out string directory)
    {
        directory = "";
        try
        {
            var candidate = PathContainment.ResolveDirectChild(modsRoot, modId);
            if (HasReparseComponent(candidate)) return false;
            directory = candidate;
            return true;
        }
        catch (Exception ex) when (IsPathException(ex)) { return false; }
    }

    public static bool CanDeleteDirectoryTree(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || HasReparseComponent(directory)) return false;
            var stack = new Stack<string>();
            stack.Push(directory);
            while (stack.Count > 0)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(stack.Pop()))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                    if ((attributes & FileAttributes.Directory) != 0) stack.Push(entry);
                }
            }
            return true;
        }
        catch (Exception ex) when (IsPathException(ex)) { return false; }
    }

    private static bool HasReparseComponent(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        return false;
    }

    private static bool IsPathException(Exception ex) =>
        ex is ArgumentException or NotSupportedException or IOException
            or UnauthorizedAccessException or System.Security.SecurityException;
}
