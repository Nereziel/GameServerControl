using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Filesystem-level mod installation: prepare the complete payload off to the
/// side, then replace in-place with backups for rollback. Never remove an old
/// installation until the new payload has been fully staged.
/// </summary>
public static class ModInstallTransaction
{
    public static bool ReplaceDirectory(
        string staged, string destination, Action<string, string>? moveDirectory = null)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))
            ?? throw new InvalidOperationException("Mod folder must have a parent.");
        if (!PathContainment.IsWithinRoot(parent, destination) ||
            !PathContainment.IsWithinRoot(parent, staged))
            throw new InvalidOperationException("Mod staging and destination must share their direct parent.");
        if (!Directory.Exists(staged) || !ModRemovalSafety.CanDeleteDirectoryTree(staged))
            throw new InvalidOperationException("Staged mod folder is unavailable or contains links.");
        if (Directory.Exists(destination) && !ModRemovalSafety.CanDeleteDirectoryTree(destination))
            throw new InvalidOperationException("Existing mod folder contains unsafe links.");

        var move = moveDirectory ?? Directory.Move;
        var previous = Path.Combine(parent, ".gsc-mod-prev-" + Guid.NewGuid().ToString("N"));
        var hadOld = Directory.Exists(destination);
        var oldMoved = false;
        try
        {
            if (hadOld)
            {
                move(destination, previous);
                oldMoved = true;
            }
            move(staged, destination);
        }
        catch
        {
            if (oldMoved && Directory.Exists(previous) && !Directory.Exists(destination))
            {
                try { Directory.Move(previous, destination); }
                catch
                {
                    // Preserve previous rather than deleting the last recoverable copy.
                }
            }
            throw;
        }

        if (!oldMoved) return false;
        try { Directory.Delete(previous, recursive: true); return false; }
        catch { return true; } // Replacement committed; previous retained for manual cleanup.
    }

    public static bool ReplaceFiles(string allowedRoot, IEnumerable<(string Source, string Destination)> replacements)
    {
        var root = Path.GetFullPath(allowedRoot);
        var tx = Guid.NewGuid().ToString("N");
        var staged = replacements.Select((pair, i) =>
        {
            if (!File.Exists(pair.Source))
                throw new FileNotFoundException("Staged mod file is missing.", pair.Source);
            var full = Path.GetFullPath(pair.Destination);
            var relative = Path.GetRelativePath(root, full);
            if (!ModRemovalSafety.TryResolveInstalledFile(root, root, relative, out var verified) ||
                !string.Equals(verified, full, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe mod file destination.");
            return new FileSwap(pair.Source, full, full + ".gsc-prev-" + tx + "-" + i);
        }).ToArray();

        try
        {
            foreach (var file in staged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                if (File.Exists(file.Destination))
                {
                    File.Move(file.Destination, file.Previous);
                    file.PreviousMoved = true;
                }
                // Copy from fully prepared extraction/download source.
                File.Copy(file.Source, file.Destination, overwrite: false);
                file.NewWritten = true;
            }
        }
        catch
        {
            foreach (var file in staged.Reverse())
            {
                try
                {
                    if (File.Exists(file.Destination) && (file.NewWritten || file.PreviousMoved))
                        File.Delete(file.Destination);
                    if (file.PreviousMoved && File.Exists(file.Previous))
                        File.Move(file.Previous, file.Destination);
                }
                catch
                {
                    // On failed rollback retain the .gsc-prev file for recovery.
                }
            }
            throw;
        }

        var retainedOriginals = false;
        foreach (var file in staged.Where(f => f.PreviousMoved))
        {
            try { File.Delete(file.Previous); }
            catch { retainedOriginals = true; }
        }
        return retainedOriginals;
    }

    private sealed record FileSwap(string Source, string Destination, string Previous)
    {
        public bool PreviousMoved { get; set; }
        public bool NewWritten { get; set; }
    }
}
