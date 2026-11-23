namespace GameServerControl.Agent.Servers;

/// <summary>
/// Publishes complete archives only: temporary ZIP bytes are invisible to backup
/// listings and the metadata sidecar is written before the final atomic rename.
/// Interrupted writers never leave a published ZIP without a valid sidecar.
/// </summary>
public static class BackupArchivePublication
{
    public static async Task PublishAsync(
        string archivePath,
        Func<string, CancellationToken, Task> writeArchive,
        Func<CancellationToken, Task> writeMetadata,
        CancellationToken ct)
    {
        var stagingPath = archivePath + ".gsc-staging-" + Guid.NewGuid().ToString("N");
        var published = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            await writeArchive(stagingPath, ct);
            ct.ThrowIfCancellationRequested();
            await writeMetadata(ct);
            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, archivePath);
            published = true;
        }
        finally
        {
            if (!published)
            {
                TryDelete(stagingPath);
                TryDelete(archivePath + ".meta.json");
                TryDelete(archivePath + ".meta.json.tmp");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Never hide the original archive creation error or cancellation. */ }
    }
}
