using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Copies an already finished complete backup to an administrator-mounted
/// off-machine directory. Copies are streamed, SHA-256-checked, and published
/// through atomic renames. The local backup is never mutated or removed.
/// </summary>
public static class OffsiteBackupMirror
{
    private static readonly Regex ServerId = new("^[a-z0-9][a-z0-9-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static async Task<string> CopyAsync(
        string archivePath, string offsiteRoot, string serverId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(serverId) || !ServerId.IsMatch(serverId))
            throw new ArgumentException("Invalid server ID for offsite backup.", nameof(serverId));
        if (string.IsNullOrWhiteSpace(offsiteRoot) || !Path.IsPathFullyQualified(offsiteRoot))
            throw new ArgumentException("Offsite backup root must be an absolute mounted filesystem path.", nameof(offsiteRoot));
        if (string.IsNullOrWhiteSpace(archivePath) || !Path.IsPathFullyQualified(archivePath))
            throw new ArgumentException("Source backup archive path must be absolute.", nameof(archivePath));

        var source = Path.GetFullPath(archivePath);
        var metadata = source + ".meta.json";
        if (!source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(source) || !File.Exists(metadata))
            throw new InvalidDataException("Complete archive and metadata sidecar are required for offsite copy.");
        RejectLink(source);
        RejectLink(metadata);

        using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(metadata, ct)))
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("SkippedFiles", out var skipped) ||
                skipped.ValueKind != JsonValueKind.Number ||
                !skipped.TryGetInt32(out var skippedCount) || skippedCount != 0)
                throw new InvalidDataException("Incomplete or invalid backup metadata cannot be mirrored.");
        }

        var mount = Path.GetFullPath(offsiteRoot).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (mount == Path.GetPathRoot(mount))
            throw new ArgumentException("Offsite backup destination cannot be a filesystem root.");
        if (!Directory.Exists(mount))
            throw new DirectoryNotFoundException("Offsite backup directory is not available; mount it first.");
        RejectLink(mount);

        var outputDir = Path.Combine(mount, serverId);
        var proposedFile = Path.GetFullPath(Path.Combine(outputDir, Path.GetFileName(source)));
        if (string.Equals(proposedFile, source,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Local backup directory cannot also be used as offsite destination.");

        // A marker must exist on the mounted storage. If an SMB/NFS mount
        // disappears, the underlying local directory must not silently
        // receive supposedly remote backup copies.
        var marker = Path.Combine(mount, ".gsc-offsite-ready");
        if (!File.Exists(marker))
            throw new InvalidOperationException(
                "Offsite storage verification marker is missing. Check that the network share is mounted.");
        RejectLink(marker);
        if (Directory.Exists(outputDir)) RejectLink(outputDir);
        else Directory.CreateDirectory(outputDir);

        var output = Path.Combine(outputDir, Path.GetFileName(source));
        // Publish metadata first. If the archive copy is interrupted, an
        // orphan sidecar is harmless; never expose a ZIP without its metadata.
        await CopyVerifiedAsync(metadata, output + ".meta.json", ct);
        await CopyVerifiedAsync(source, output, ct);
        return output;
    }

    private static async Task CopyVerifiedAsync(string source, string target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sourceHash = await HashAsync(source, ct);
        if (File.Exists(target))
        {
            RejectLink(target);
            var existingHash = await HashAsync(target, ct);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, existingHash))
                throw new IOException("Existing offsite copy differs from the local archive; refusing to overwrite it.");
            return;
        }
        var temp = target + ".gsc-staging-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
            }
            var tempHash = await HashAsync(temp, ct);
            // Recheck source: active data must not have changed during the copy.
            var confirmedSource = await HashAsync(source, ct);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, tempHash) ||
                !CryptographicOperations.FixedTimeEquals(sourceHash, confirmedSource))
                throw new InvalidDataException("Offsite transfer failed SHA-256 integrity verification.");
            ct.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static async Task<byte[]> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, ct);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked files or directories cannot be used for offsite backups.");
    }
}
