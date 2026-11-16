using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// File and directory replacements keep previous mod data until the metadata
/// sidecar has been durably written. Disposing an uncommitted replacement rolls
/// files back, rather than leaving a newer binary with older metadata.
/// </summary>
public static class ModInstallTransaction
{
    public static bool MatchesExpectedModId(string? expectedModId, string? actualModId) =>
        expectedModId is null ||
        (!string.IsNullOrWhiteSpace(actualModId) &&
         string.Equals(expectedModId, actualModId, StringComparison.OrdinalIgnoreCase));

    public interface IReplacement : IDisposable
    {
        // Metadata must be atomically persisted by the callback. If it throws,
        // disposal restores original files and the error reaches the API caller.
        // Returns true when old files remain because post-commit cleanup failed.
        bool Complete(Action? writeMetadata = null);
    }

    public static bool ReplaceDirectory(
        string staged, string destination, Action<string, string>? moveDirectory = null)
    {
        using var replacement = BeginDirectory(staged, destination, moveDirectory);
        return replacement.Complete();
    }

    public static bool ReplaceFiles(string allowedRoot, IEnumerable<(string Source, string Destination)> replacements)
    {
        using var replacement = BeginFiles(allowedRoot, replacements);
        return replacement.Complete();
    }

    public static IReplacement BeginDirectory(
        string staged, string destination, Action<string, string>? moveDirectory = null)
    {
        var destinationFull = Path.GetFullPath(destination);
        var stageFull = Path.GetFullPath(staged);
        var parent = Path.GetDirectoryName(destinationFull)
            ?? throw new InvalidOperationException("Mod folder must have a parent.");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(stageFull), parent, comparison) ||
            !PathContainment.IsWithinRoot(parent, destinationFull) ||
            !PathContainment.IsWithinRoot(parent, stageFull) ||
            string.Equals(stageFull, destinationFull, comparison))
            throw new InvalidOperationException("Mod staging and destination must be different direct children.");
        if (!Directory.Exists(stageFull) || !ModRemovalSafety.CanDeleteDirectoryTree(stageFull))
            throw new InvalidOperationException("Staged mod folder is unavailable or contains links.");
        if (Directory.Exists(destinationFull) && !ModRemovalSafety.CanDeleteDirectoryTree(destinationFull))
            throw new InvalidOperationException("Existing mod folder contains unsafe links.");

        var move = moveDirectory ?? Directory.Move;
        var previous = Path.Combine(parent, ".gsc-mod-prev-" + Guid.NewGuid().ToString("N"));
        var hadOld = Directory.Exists(destinationFull);
        var previousMoved = false;
        try
        {
            if (hadOld)
            {
                move(destinationFull, previous);
                previousMoved = true;
            }
            move(stageFull, destinationFull);
            return new DirectoryReplacement(stageFull, destinationFull, previous, previousMoved);
        }
        catch
        {
            if (previousMoved && Directory.Exists(previous) && !Directory.Exists(destinationFull))
            {
                // The old directory must not be discarded if this rollback fails.
                move(previous, destinationFull);
            }
            throw;
        }
    }

    public static IReplacement BeginFiles(
        string allowedRoot, IEnumerable<(string Source, string Destination)> replacements)
    {
        var root = Path.GetFullPath(allowedRoot);
        // IsWithinRoot intentionally excludes the root itself. Resolve from the
        // parent so a file directly inside allowedRoot can be checked safely.
        var parent = Path.GetDirectoryName(root)
            ?? throw new InvalidOperationException("A mod directory cannot be a filesystem root.");
        var txId = Guid.NewGuid().ToString("N");
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = replacements.Select((pair, i) =>
        {
            if (!File.Exists(pair.Source))
                throw new FileNotFoundException("Staged mod file is missing.", pair.Source);
            var full = Path.GetFullPath(pair.Destination);
            var relative = Path.GetRelativePath(parent, full);
            if (!ModRemovalSafety.TryResolveInstalledFile(parent, root, relative, out var verified) ||
                !string.Equals(verified, full, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !seen.Add(full))
                throw new InvalidOperationException("Unsafe or duplicate mod file destination.");
            if (string.Equals(Path.GetFullPath(pair.Source), full, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Mod cannot replace itself.");
            return new FileSwap(pair.Source, full, full + ".gsc-prev-" + txId + "-" + i);
        }).ToArray();
        var transaction = new FileReplacement(pending);
        try
        {
            transaction.Apply();
            return transaction;
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    private sealed class DirectoryReplacement(
        string staged, string destination, string previous, bool previousMoved) : IReplacement
    {
        private bool _committed;
        private bool _disposed;

        public bool Complete(Action? writeMetadata = null)
        {
            if (_disposed || _committed) throw new InvalidOperationException("Mod replacement already completed.");
            writeMetadata?.Invoke();
            _committed = true; // Cleanup must never trigger rollback after metadata commits.
            if (!previousMoved) return false;
            try { Directory.Delete(previous, recursive: true); return false; }
            catch { return true; } // The old copy remains available for manual recovery.
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_committed) return;
            // A metadata failure happens after the new stage was moved into place.
            // Move it back so the previous directory can be restored.
            if (Directory.Exists(destination))
            {
                if (Directory.Exists(staged))
                    throw new IOException("Rollback stage already exists; old mod copy was retained at " + previous);
                Directory.Move(destination, staged);
            }
            if (previousMoved && Directory.Exists(previous))
                Directory.Move(previous, destination);
        }
    }

    private sealed class FileSwap(string source, string destination, string previous)
    {
        public string Source { get; } = source;
        public string Destination { get; } = destination;
        public string Previous { get; } = previous;
        public bool PreviousMoved { get; set; }
        public bool CopyAttempted { get; set; }
    }

    private sealed class FileReplacement(FileSwap[] entries) : IReplacement
    {
        private bool _committed;
        private bool _disposed;

        public void Apply()
        {
            foreach (var entry in entries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Destination)!);
                if (File.Exists(entry.Destination))
                {
                    File.Move(entry.Destination, entry.Previous);
                    entry.PreviousMoved = true;
                }
                entry.CopyAttempted = true;
                File.Copy(entry.Source, entry.Destination, overwrite: false);
            }
        }

        public bool Complete(Action? writeMetadata = null)
        {
            if (_disposed || _committed) throw new InvalidOperationException("Mod replacement already completed.");
            writeMetadata?.Invoke();
            _committed = true;
            var retained = false;
            foreach (var entry in entries.Where(e => e.PreviousMoved))
            {
                try { File.Delete(entry.Previous); }
                catch { retained = true; }
            }
            return retained;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_committed) return;
            var failures = new List<string>();
            foreach (var entry in entries.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.CopyAttempted && File.Exists(entry.Destination))
                        File.Delete(entry.Destination);
                    if (entry.PreviousMoved && File.Exists(entry.Previous))
                        File.Move(entry.Previous, entry.Destination);
                }
                catch (Exception)
                {
                    failures.Add(entry.Destination);
                    // Keep the previous copy for manual recovery, never delete it.
                }
            }
            if (failures.Count != 0)
                throw new IOException("Mod rollback could not restore: " + string.Join(", ", failures));
        }
    }
}
