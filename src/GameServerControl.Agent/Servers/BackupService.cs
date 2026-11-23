using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Owns backup creation, retention, listing, restore and deletion. Archive bytes remain on the
/// Agent host; browser/cloud clients only invoke small control-plane API calls.
/// </summary>
public sealed class BackupService
{
    private sealed record BackupMetadata(
        string? Label,
        BackupKind Kind,
        DateTimeOffset CreatedAt,
        int FileCount,
        int SkippedFiles);

    private sealed record CheckpointMetadata(
        string CheckpointName,
        string? Label,
        BackupKind Kind,
        DateTimeOffset CreatedAt);

    private sealed record SaveTarget(string Path, string Prefix);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HypervService _hv;
    private readonly LocalProcessService _local;
    private readonly ServerRegistry _registry;
    private readonly ServerLifecycleGate _lifecycleGate;
    private readonly ServerStore _store;
    private readonly StatusTracker _tracker;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        HypervService hv,
        LocalProcessService local,
        ServerRegistry registry,
        ServerLifecycleGate lifecycleGate,
        ServerStore store,
        StatusTracker tracker,
        IConfiguration cfg,
        ILogger<BackupService> logger)
    {
        _hv = hv;
        _local = local;
        _registry = registry;
        _lifecycleGate = lifecycleGate;
        _store = store;
        _tracker = tracker;
        _cfg = cfg;
        _logger = logger;
    }

    private string BackupRoot => _cfg["Agent:BackupRoot"] ??
        (OperatingSystem.IsWindows() ? @"C:\GameServerControl\Backups" : "/var/lib/gameservercontrol/backups");

    private async Task<string?> MirrorOffsiteIfConfiguredAsync(
        ServerDef def, string? localArchive, CancellationToken ct)
    {
        var target = _cfg["Agent:OffsiteBackupRoot"];
        if (string.IsNullOrWhiteSpace(target)) return null;
        try
        {
            if (localArchive is null)
                throw new InvalidDataException("Local archive is unavailable for mirroring.");
            await OffsiteBackupMirror.CopyAsync(localArchive, target, def.Id, ct);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Offsite backup verification failed for {ServerId}", def.Id);
            return "Offsite backup failed integrity or storage checks; check Agent logs and mounted backup storage.";
        }
    }

    // Local retention is independent of offsite availability. Once a complete
    // archive exists, every exit path (including a cancelled mirror) must apply
    // the local retention policy. Use a non-cancelled token for this short local
    // housekeeping step so cancellation cannot grow backups without bound.
    private async Task<string?> MirrorAndPruneBareMetalAsync(
        ServerDef def, string? localArchive, CancellationToken ct)
    {
        try
        {
            return await MirrorOffsiteIfConfiguredAsync(def, localArchive, ct);
        }
        finally
        {
            await PruneBareMetalAsync(def, CancellationToken.None);
        }
    }

    private int DefaultBackupRetention
        => int.TryParse(_cfg["Agent:BackupRetention"], out var value) && value > 0
            ? Math.Clamp(value, 1, 200)
            : 10;

    private int EffectiveBackupRetention(ServerDef def)
        => Math.Clamp(def.BackupRetention ?? DefaultBackupRetention, 1, 200);

    public BackupSettings GetSettings(string id)
    {
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);
        var fallback = DefaultBackupRetention;
        return new BackupSettings(EffectiveBackupRetention(def), def.BackupRetention.HasValue, fallback);
    }

    public BackupSettings SaveSettings(string id, int retention)
    {
        if (retention is < 1 or > 200)
            throw new ArgumentException("Backup retention must be between 1 and 200.", nameof(retention));
        // Apply only the requested field against the latest ServerStore state.
        // A previous Registry.Get() snapshot may already be stale.
        var updated = _store.SetBackupRetention(id, retention);
        return new BackupSettings(updated.BackupRetention!.Value, true, DefaultBackupRetention);
    }

    public Task<ActionResult> CreateAsync(string id, CancellationToken ct = default)
        => CreateAsync(id, null, ct);

    public async Task<ActionResult> CreateAsync(string id, string? label, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        try { label = NormalizeLabel(label); }
        catch (ArgumentException ex) { return new ActionResult(false, ex.Message, corr); }

        if (def.HostingMode == HostingMode.BareMetal)
        {
            ArchiveCreateResult created;
            try
            {
                created = await CreateBareMetalArchiveAsync(def, label, BackupKind.Archive, requireComplete: false, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException)
            {
                return new ActionResult(false, ex.Message, corr);
            }

            if (!created.Success)
                return new ActionResult(false, created.Error ?? "Backup failed", corr);

            var mb = (created.SizeBytes ?? 0) / 1024d / 1024d;
            if (created.Metadata?.SkippedFiles is > 0)
            {
                await PruneBareMetalAsync(def, ct);
                return new ActionResult(false,
                    $"Incomplete backup retained for inspection: {Path.GetFileName(created.Path)} ({mb:0.##} MB); " +
                    $"{created.Metadata.SkippedFiles} file(s) were skipped. Restore is disabled. Complete backups are preserved independently.",
                    corr);
            }
            var offsiteProblem = await MirrorAndPruneBareMetalAsync(def, created.Path, ct);
            _tracker.Update(id, state => state with { LastBackupAt = DateTimeOffset.UtcNow });
            if (offsiteProblem is not null)
                return new ActionResult(false,
                    $"Local backup retained: {Path.GetFileName(created.Path)}. " + offsiteProblem, corr);
            return new ActionResult(true,
                $"Backup created: {Path.GetFileName(created.Path)} ({mb:0.##} MB).", corr);
        }

        if (!OperatingSystem.IsWindows())
            return new ActionResult(false, "Hyper-V backups require Windows.", corr);

        var name = $"{def.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        if (!await _hv.CreateCheckpointAsync(def.VmName, name, ct))
            return new ActionResult(false, "Checkpoint failed", corr);

        var meta = new CheckpointMetadata(name, label, BackupKind.HyperVCheckpoint, DateTimeOffset.UtcNow);
        try
        {
            await WriteCheckpointMetadataAsync(def, meta, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checkpoint metadata write failed for {ServerId}/{Checkpoint}", def.Id, name);
            await _hv.DeleteCheckpointAsync(def.VmName, name, ct);
            return new ActionResult(false, "Checkpoint metadata write failed; checkpoint was removed.", corr);
        }

        await PruneHyperVAsync(def, ct);
        _tracker.Update(id, s => s with { LastBackupAt = DateTimeOffset.UtcNow });
        return new ActionResult(true, "Checkpoint created: " + name, corr);
    }

    /// <summary>
    /// Create a complete, restorable snapshot before changing installed game files.
    /// Abort updates when a configured save cannot be read instead of silently
    /// producing a partial archive.
    /// </summary>
    public async Task<ActionResult> CreatePreUpdateAsync(string id, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);
        if (def.HostingMode != HostingMode.BareMetal)
            return await CreateAsync(id, "Pre-update backup", ct);

        ArchiveCreateResult created;
        try
        {
            // A pre-update snapshot must include every configured save directory.
            var saveTargets = GetSaveTargets(def, requireAnyExisting: true);
            if (saveTargets.Any(target => !Directory.Exists(target.Path)))
                return new ActionResult(false, "One or more configured save directories are missing.", corr);
            created = await CreateBareMetalArchiveAsync(def, "Pre-update backup",
                BackupKind.SafetyArchive, requireComplete: true, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException)
        {
            return new ActionResult(false, ex.Message, corr);
        }

        if (!created.Success)
            return new ActionResult(false, created.Error ?? "Pre-update backup failed", corr);
        var offsiteProblem = await MirrorAndPruneBareMetalAsync(def, created.Path, ct);
        _tracker.Update(id, current => current with { LastBackupAt = DateTimeOffset.UtcNow });
        if (offsiteProblem is not null)
            return new ActionResult(false,
                "Local pre-update snapshot retained, but update was cancelled. " + offsiteProblem, corr);
        return new ActionResult(true, "Pre-update backup: " + Path.GetFileName(created.Path), corr);
    }

    public async Task<List<BackupInfo>> ListAsync(string id, CancellationToken ct = default)
    {
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);
        return def.HostingMode == HostingMode.BareMetal
            ? ListBareMetal(def)
            : await ListHyperVAsync(def, ct);
    }

    public Task<ActionResult> RestoreAsync(string id, string backupName, CancellationToken ct = default)
        => _lifecycleGate.RunExclusiveAsync(id, () => RestoreCoreAsync(id, backupName, ct), ct);

    // Called with the shared per-server lifecycle gate held. This includes the
    // stopped-process probe, safety snapshot, archive staging, swap and rollback.
    private async Task<ActionResult> RestoreCoreAsync(string id, string backupName, CancellationToken ct)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        try
        {
            return def.HostingMode == HostingMode.BareMetal
                ? await RestoreBareMetalFromArchiveAsync(def, backupName, corr, ct)
                : await RestoreHyperVAsync(def, backupName, corr, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or DirectoryNotFoundException)
        {
            return new ActionResult(false, ex.Message, corr);
        }
    }

    public async Task<bool> DeleteAsync(string id, string backupName, CancellationToken ct = default)
    {
        var def = _registry.Get(id);
        if (def is null) return false;

        if (def.HostingMode == HostingMode.BareMetal)
        {
            var safeName = BackupSafety.ValidateBackupFileName(backupName);
            var owned = ListBareMetal(def).Any(x =>
                string.Equals(x.CheckpointName, safeName, StringComparison.OrdinalIgnoreCase));
            if (!owned) return false;

            var path = BackupSafety.ResolveUnderRoot(ServerBackupDir(def), safeName);
            if (!File.Exists(path)) return false;
            try
            {
                File.Delete(path);
                DeleteMetadata(path);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delete backup failed for {Path}", path);
                return false;
            }
        }

        var checkpoint = await FindOwnedCheckpointAsync(def, backupName, ct);
        if (checkpoint is null) return false;
        var ok = await _hv.DeleteCheckpointAsync(def.VmName, checkpoint.CheckpointName, ct);
        if (ok) DeleteCheckpointMetadata(def, checkpoint.CheckpointName);
        return ok;
    }

    private async Task<ActionResult> RestoreBareMetalFromArchiveAsync(ServerDef def, string backupName, string corr, CancellationToken ct)
    {
        var (probeOk, runningPid) = await _local.FindProcessAsync(def, ct);
        if (!probeOk)
            return new ActionResult(false, "Could not verify that the server process is stopped.", corr);
        if (runningPid is not null)
            return new ActionResult(false, "Stop the server before restoring a backup.", corr);

        var safeName = BackupSafety.ValidateBackupFileName(backupName);
        var owned = ListBareMetal(def).FirstOrDefault(x =>
            string.Equals(x.CheckpointName, safeName, StringComparison.OrdinalIgnoreCase));
        if (owned is null)
            return new ActionResult(false, "Backup not found or not owned by GameServerControl: " + safeName, corr);
        if (!owned.Restorable || owned.SkippedFiles is > 0)
            return new ActionResult(false, "Backup is incomplete or unverified (missing/invalid metadata or skipped files); restore is disabled to prevent data loss.", corr);

        var zipPath = BackupSafety.ResolveUnderRoot(ServerBackupDir(def), safeName);
        var targets = GetSaveTargets(def, requireAnyExisting: false);
        ValidateArchiveEntries(zipPath, targets, owned.FileCount);

        var safety = await CreateBareMetalArchiveAsync(
            def,
            "Pre-restore safety copy",
            BackupKind.SafetyArchive,
            requireComplete: true,
            ct);
        if (!safety.Success)
            return new ActionResult(false, "Safety backup failed: " + (safety.Error ?? "unknown error") + ". Restore was not started.", corr);
        var offsiteProblem = await MirrorAndPruneBareMetalAsync(def, safety.Path, ct);
        if (offsiteProblem is not null)
            return new ActionResult(false,
                "Local pre-restore safety snapshot retained, but restore was not started. " + offsiteProblem, corr);

        var swaps = targets.Select(t => new RestoreSwapStep
        {
            Target = t.Path,
            Stage = t.Path + ".gsc-restore-" + corr,
            Old = t.Path + ".gsc-old-" + corr,
            HadOriginal = Directory.Exists(t.Path)
        }).ToList();

        try
        {
            ExtractArchiveToStages(zipPath, targets, swaps, ct);

            // Another process manager (outside the Agent lifecycle) could start
            // the game while the safety archive was being created. Recheck
            // immediately before swapping save directories.
            var (stillProbeOk, stillRunningPid) = await _local.FindProcessAsync(def, ct);
            if (!stillProbeOk || stillRunningPid is not null)
                throw new InvalidOperationException(
                    "Game process state changed or could not be verified. Restore was aborted before swapping saves.");

            BackupRestoreSwapTransactions.Commit(swaps, ct);

            // All swap steps must succeed before any original data is deleted.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore swap failed for {ServerId}; attempting rollback", def.Id);
            BackupRestoreSwapTransactions.Rollback(swaps, _logger);
            CleanupStages(swaps); // Only staging paths. Never delete a retained .gsc-old copy here.
            return new ActionResult(false, "Restore failed and rollback was attempted: " + ex.Message, corr);
        }

        // Commit completed: cleanup failures must NOT trigger a rollback. The restored
        // saves are already live and other .Old copies may have been deleted.
        CleanupStages(swaps);
        var retainedOriginals = BackupRestoreSwapTransactions.CleanupCommitted(swaps, _logger);
        return new ActionResult(true,
            $"Restored from {safeName}. Safety copy: {Path.GetFileName(safety.Path)}" +
            (retainedOriginals ? " Warning: one or more old save directories could not be removed; inspect .gsc-old folders before removing them." : ""), corr);
    }

    private async Task<ActionResult> RestoreHyperVAsync(ServerDef def, string checkpointName, string corr, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new ActionResult(false, "Hyper-V restore requires Windows.", corr);

        var state = await _hv.GetStateAsync(def.VmName, ct);
        if (state != VmState.Off)
            return new ActionResult(false, "Turn off the Hyper-V VM before restoring a checkpoint.", corr);

        var checkpoint = await FindOwnedCheckpointAsync(def, checkpointName, ct);
        if (checkpoint is null)
            return new ActionResult(false, "Checkpoint not found or not owned by GameServerControl.", corr);

        var safetyName = $"{def.Id}-prerestore-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}";
        if (!await _hv.CreateCheckpointAsync(def.VmName, safetyName, ct))
            return new ActionResult(false, "Safety backup failed: could not create pre-restore checkpoint. Restore was not started.", corr);

        try
        {
            await WriteCheckpointMetadataAsync(def,
                new CheckpointMetadata(safetyName, "Pre-restore safety copy", BackupKind.SafetyCheckpoint, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Safety checkpoint metadata write failed for {ServerId}", def.Id);
            await _hv.DeleteCheckpointAsync(def.VmName, safetyName, ct);
            return new ActionResult(false, "Safety backup failed: metadata could not be persisted. Restore was not started.", corr);
        }

        await PruneHyperVAsync(def, ct);
        // A VM may have been powered on outside GameServerControl during the
        // safety checkpoint. Never apply a restore to a running/unknown VM.
        if (await _hv.GetStateAsync(def.VmName, ct) != VmState.Off)
            return new ActionResult(false,
                "VM state changed during pre-restore backup. Restore was not started.", corr);
        var restored = await _hv.RestoreCheckpointAsync(def.VmName, checkpoint.CheckpointName, ct);
        return restored
            ? new ActionResult(true, $"Restored checkpoint {checkpoint.CheckpointName}. Safety copy: {safetyName}", corr)
            : new ActionResult(false, $"Checkpoint restore failed. Safety copy remains: {safetyName}", corr);
    }

    private sealed record ArchiveCreateResult(
        bool Success,
        string? Path,
        long? SizeBytes,
        BackupMetadata? Metadata,
        string? Error);

    private async Task<ArchiveCreateResult> CreateBareMetalArchiveAsync(
        ServerDef def,
        string? label,
        BackupKind kind,
        bool requireComplete,
        CancellationToken ct)
    {
        var targets = GetSaveTargets(def, requireAnyExisting: kind == BackupKind.Archive);
        var serverDir = ServerBackupDir(def);
        Directory.CreateDirectory(serverDir);
        var safety = kind == BackupKind.SafetyArchive;
        // Distinct paths prevent concurrent creations from sharing a metadata sidecar.
        var nonce = Guid.NewGuid().ToString("N")[..12];
        var baseName = safety
            ? $"{def.Id}-{(label == "Pre-update backup" ? "preupdate" : "prerestore")}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{nonce}.zip"
            : $"{def.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{nonce}.zip";
        var zipPath = UniqueArchivePath(serverDir, baseName);
        var createdAt = DateTimeOffset.UtcNow;
        var fileCount = 0;
        var skipped = 0;
        BackupMetadata? metadata = null;

        try
        {
            await BackupArchivePublication.PublishAsync(zipPath,
                async (stagingPath, cancellation) =>
                {
                    await Task.Run(async () =>
                    {
                        await using var fs = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
                        foreach (var target in targets)
                        {
                            if (!Directory.Exists(target.Path)) continue;
                            foreach (var file in Directory.EnumerateFiles(target.Path, "*", SearchOption.AllDirectories))
                            {
                                cancellation.ThrowIfCancellationRequested();
                                var rel = Path.GetRelativePath(target.Path, file);
                                var entryName = BackupSafety.NormalizeArchiveEntry(target.Prefix + "/" + rel.Replace('\\', '/'));
                                try
                                {
                                    var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                                    using var output = entry.Open();
                                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                                    await input.CopyToAsync(output, cancellation);
                                    fileCount++;
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    if (requireComplete) throw;
                                    skipped++;
                                    _logger.LogWarning("Skipping unavailable backup file {File}: {Message}", file, ex.Message);
                                }
                            }
                        }
                    }, cancellation);
                },
                async cancellation =>
                {
                    metadata = new BackupMetadata(label, kind, createdAt, fileCount, skipped);
                    await WriteBackupMetadataAsync(zipPath, metadata, cancellation);
                },
                ct);

            return new ArchiveCreateResult(true, zipPath, new FileInfo(zipPath).Length, metadata, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ArchiveCreateResult(false, null, null, null, ex.Message);
        }
    }

    private List<BackupInfo> ListBareMetal(ServerDef def)
    {
        var dir = ServerBackupDir(def);
        var result = new List<BackupInfo>();
        if (!Directory.Exists(dir)) return result;

        foreach (var file in new DirectoryInfo(dir).GetFiles("*.zip"))
        {
            if (!file.Name.StartsWith(def.Id + "-", StringComparison.OrdinalIgnoreCase)) continue;
            BackupMetadata? meta = null;
            try { meta = ReadBackupMetadata(file.FullName); }
            catch (Exception ex) { _logger.LogWarning(ex, "Ignoring invalid backup metadata for {File}", file.FullName); }

            var kind = meta?.Kind ?? (file.Name.Contains("-prerestore-", StringComparison.OrdinalIgnoreCase)
                ? BackupKind.SafetyArchive
                : BackupKind.Archive);
            result.Add(new BackupInfo(
                file.Name,
                def.Id,
                meta?.CreatedAt ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Name,
                file.Length,
                kind,
                meta?.Label,
                meta?.FileCount,
                meta?.SkippedFiles,
                Restorable: HasTrustedArchiveMetadata(meta)));
        }

        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    // Metadata-less legacy ZIPs stay visible for inspection, but fail closed:
    // a structurally valid ZIP does not prove the backup contained every save.
    private static bool HasTrustedArchiveMetadata(BackupMetadata? metadata)
        => metadata is { FileCount: >= 0, SkippedFiles: 0 }
            && (metadata.Kind == BackupKind.Archive || metadata.Kind == BackupKind.SafetyArchive)
            && metadata.CreatedAt != default;

    private async Task<List<BackupInfo>> ListHyperVAsync(ServerDef def, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new List<BackupInfo>();
        var all = await _hv.ListCheckpointsAsync(def.Id, def.VmName, ct);
        var result = new List<BackupInfo>();
        foreach (var item in all)
        {
            if (!IsOwnedCheckpointName(def, item.CheckpointName)) continue;
            var meta = ReadCheckpointMetadata(def, item.CheckpointName);
            var kind = meta?.Kind ?? (item.CheckpointName.Contains("-prerestore-", StringComparison.OrdinalIgnoreCase)
                ? BackupKind.SafetyCheckpoint
                : BackupKind.HyperVCheckpoint);
            result.Add(item with
            {
                Kind = kind,
                Label = meta?.Label,
                CreatedAt = meta?.CreatedAt ?? item.CreatedAt,
                Restorable = true
            });
        }
        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

    private async Task<BackupInfo?> FindOwnedCheckpointAsync(ServerDef def, string checkpointName, CancellationToken ct)
    {
        ValidateCheckpointName(checkpointName);
        var list = await ListHyperVAsync(def, ct);
        return list.FirstOrDefault(x => string.Equals(x.CheckpointName, checkpointName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PruneBareMetalAsync(ServerDef def, CancellationToken ct)
    {
        var list = ListBareMetal(def);
        foreach (var item in BackupRetentionPolicy.SelectBareMetalEvictions(list, EffectiveBackupRetention(def)))
        {
            ct.ThrowIfCancellationRequested();
            DeleteArchiveByName(def, item.CheckpointName);
        }
        await Task.CompletedTask;
    }

    private async Task PruneHyperVAsync(ServerDef def, CancellationToken ct)
    {
        var list = await ListHyperVAsync(def, ct);
        foreach (var item in list.Where(x => x.Kind == BackupKind.HyperVCheckpoint).Skip(EffectiveBackupRetention(def)))
        {
            ct.ThrowIfCancellationRequested();
            if (await _hv.DeleteCheckpointAsync(def.VmName, item.CheckpointName, ct))
                DeleteCheckpointMetadata(def, item.CheckpointName);
        }
        foreach (var item in list.Where(x => x.Kind == BackupKind.SafetyCheckpoint).Skip(3))
        {
            ct.ThrowIfCancellationRequested();
            if (await _hv.DeleteCheckpointAsync(def.VmName, item.CheckpointName, ct))
                DeleteCheckpointMetadata(def, item.CheckpointName);
        }
    }

    private void ValidateArchiveEntries(string zipPath, IReadOnlyList<SaveTarget> targets, int? expectedFileCount)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var actualFiles = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = BackupSafety.NormalizeArchiveEntry(entry.FullName);
            if (!targets.Any(t => normalized.Equals(t.Prefix, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(t.Prefix + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Backup contains unexpected path '{entry.FullName}'.");
            if (!string.IsNullOrEmpty(entry.Name)) actualFiles++;
        }
        if (expectedFileCount is int expected && actualFiles != expected)
            throw new InvalidDataException("Backup ZIP file count does not match the saved metadata.");
    }

    private void ExtractArchiveToStages(
        string zipPath,
        IReadOnlyList<SaveTarget> targets,
        IReadOnlyList<RestoreSwapStep> swaps,
        CancellationToken ct)
    {
        foreach (var swap in swaps)
        {
            // A stale .Old path can contain the last recoverable copy from an
            // interrupted restore. Refuse to overwrite/delete it.
            if (Directory.Exists(swap.Stage) || Directory.Exists(swap.Old))
                throw new IOException("Restore staging or prior-original directory already exists: " + swap.Target);
            Directory.CreateDirectory(swap.Stage);
        }

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = BackupSafety.NormalizeArchiveEntry(entry.FullName);
            var index = -1;
            for (var i = 0; i < targets.Count; i++)
            {
                var prefix = targets[i].Prefix;
                if (normalized.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || normalized.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) throw new InvalidDataException($"Backup contains unexpected path '{entry.FullName}'.");
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var prefixLength = targets[index].Prefix.Length;
            var relative = normalized.Length == prefixLength ? "" : normalized[(prefixLength + 1)..];
            if (string.IsNullOrEmpty(relative)) continue;
            var destination = BackupSafety.ResolveUnderRoot(swaps[index].Stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: false);
        }
    }

    private static void CleanupStages(IEnumerable<RestoreSwapStep> swaps)
    {
        foreach (var swap in swaps)
        {
            // During rollback the old directory may still contain the only original
            // data if a move failed. Never delete it from generic stage cleanup.
            TryDeleteDirectory(swap.Stage);
        }
    }

    private List<SaveTarget> GetSaveTargets(ServerDef def, bool requireAnyExisting)
    {
        if (def.SaveDirs is null || def.SaveDirs.Length == 0)
            throw new InvalidOperationException("No SaveDirs configured for this server.");

        var result = new List<SaveTarget>();
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in def.SaveDirs.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var full = Path.GetFullPath(raw.Trim());
            var prefix = new DirectoryInfo(full).Name;
            if (string.IsNullOrWhiteSpace(prefix))
                throw new InvalidOperationException($"Save directory '{raw}' cannot be archived safely.");
            if (!prefixes.Add(prefix))
                throw new InvalidOperationException($"Save directories use duplicate archive prefix '{prefix}'. Rename/reconfigure them before backup or restore.");
            result.Add(new SaveTarget(full, prefix));
        }
        if (result.Count == 0)
            throw new InvalidOperationException("No valid SaveDirs configured for this server.");
        if (requireAnyExisting && !result.Any(x => Directory.Exists(x.Path)))
            throw new DirectoryNotFoundException("None of the configured save directories exist.");
        return result;
    }

    private string ServerBackupDir(ServerDef def)
        => Path.Combine(BackupRoot, def.Id);

    private static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var value = label.Trim();
        if (value.Length > 80 || value.Any(char.IsControl))
            throw new ArgumentException("Backup label must be 80 characters or fewer and contain no control characters.", nameof(label));
        return value;
    }

    private static string UniqueArchivePath(string dir, string fileName)
    {
        var candidate = Path.Combine(dir, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(dir, $"{stem}-{i}.zip");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not allocate a unique backup archive name.");
    }

    private static string MetadataPath(string zipPath) => zipPath + ".meta.json";

    private async Task WriteBackupMetadataAsync(string zipPath, BackupMetadata metadata, CancellationToken ct)
    {
        var path = MetadataPath(zipPath);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(metadata, JsonOpts), ct);
        File.Move(tmp, path, overwrite: true);
    }

    private static BackupMetadata? ReadBackupMetadata(string zipPath)
    {
        var path = MetadataPath(zipPath);
        if (!File.Exists(path)) return null;

        // Missing JSON properties otherwise deserialize to zero values, which
        // could turn a truncated sidecar into a false "complete" backup.
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("CreatedAt", out var created) || created.ValueKind != JsonValueKind.String
            || !created.TryGetDateTimeOffset(out var timestamp) || timestamp == default
            || !root.TryGetProperty("FileCount", out var fileCount) || fileCount.ValueKind != JsonValueKind.Number
            || !fileCount.TryGetInt32(out var files) || files < 0
            || !root.TryGetProperty("SkippedFiles", out var skipped) || skipped.ValueKind != JsonValueKind.Number
            || !skipped.TryGetInt32(out var skippedCount) || skippedCount < 0)
            return null;

        var metadata = JsonSerializer.Deserialize<BackupMetadata>(root.GetRawText(), JsonOpts);
        return metadata is { Kind: BackupKind.Archive or BackupKind.SafetyArchive }
            ? metadata : null;
    }

    private static string CheckpointMetadataDir(ServerDef def, string backupRoot)
        => Path.Combine(backupRoot, def.Id, ".checkpoints");

    private string CheckpointMetadataPath(ServerDef def, string checkpointName)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(checkpointName))).ToLowerInvariant();
        return Path.Combine(CheckpointMetadataDir(def, BackupRoot), hash + ".json");
    }

    private async Task WriteCheckpointMetadataAsync(ServerDef def, CheckpointMetadata metadata, CancellationToken ct)
    {
        var dir = CheckpointMetadataDir(def, BackupRoot);
        Directory.CreateDirectory(dir);
        var path = CheckpointMetadataPath(def, metadata.CheckpointName);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(metadata, JsonOpts), ct);
        File.Move(tmp, path, overwrite: true);
    }

    private CheckpointMetadata? ReadCheckpointMetadata(ServerDef def, string checkpointName)
    {
        try
        {
            var path = CheckpointMetadataPath(def, checkpointName);
            if (!File.Exists(path)) return null;
            var metadata = JsonSerializer.Deserialize<CheckpointMetadata>(File.ReadAllText(path), JsonOpts);
            return metadata is not null && string.Equals(metadata.CheckpointName, checkpointName, StringComparison.Ordinal)
                ? metadata
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid checkpoint metadata for {Checkpoint}", checkpointName);
            return null;
        }
    }

    private void DeleteCheckpointMetadata(ServerDef def, string checkpointName)
        => TryDelete(CheckpointMetadataPath(def, checkpointName));

    private void DeleteArchiveByName(ServerDef def, string backupName)
    {
        try
        {
            var safeName = BackupSafety.ValidateBackupFileName(backupName);
            var path = BackupSafety.ResolveUnderRoot(ServerBackupDir(def), safeName);
            TryDelete(path);
            DeleteMetadata(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not prune backup {Backup}", backupName);
        }
    }

    private static void DeleteMetadata(string zipPath) => TryDelete(MetadataPath(zipPath));

    private static void ValidateCheckpointName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > 240
            || name.Contains('/')
            || name.Contains('\\')
            || name.Contains(':')
            || name.Any(char.IsControl))
            throw new ArgumentException("Invalid checkpoint name.", nameof(name));
    }

    private static bool IsOwnedCheckpointName(ServerDef def, string name)
    {
        try { ValidateCheckpointName(name); }
        catch { return false; }
        return name.StartsWith(def.Id + "-", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
