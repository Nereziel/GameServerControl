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

    private sealed class RestoreSwap
    {
        public required string Target { get; init; }
        public required string Stage { get; init; }
        public required string Old { get; init; }
        public bool HadOriginal { get; init; }
        public bool OriginalMoved { get; set; }
        public bool StageMoved { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HypervService _hv;
    private readonly LocalProcessService _local;
    private readonly ServerRegistry _registry;
    private readonly ServerStore _store;
    private readonly StatusTracker _tracker;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        HypervService hv,
        LocalProcessService local,
        ServerRegistry registry,
        ServerStore store,
        StatusTracker tracker,
        IConfiguration cfg,
        ILogger<BackupService> logger)
    {
        _hv = hv;
        _local = local;
        _registry = registry;
        _store = store;
        _tracker = tracker;
        _cfg = cfg;
        _logger = logger;
    }

    private string BackupRoot => _cfg["Agent:BackupRoot"] ??
        (OperatingSystem.IsWindows() ? @"C:\GameServerControl\Backups" : "/var/lib/gameservercontrol/backups");

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
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);
        _store.Update(id, def with { BackupRetention = retention });
        return new BackupSettings(retention, true, DefaultBackupRetention);
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

            await PruneBareMetalAsync(def, ct);
            _tracker.Update(id, s => s with { LastBackupAt = DateTimeOffset.UtcNow });
            var mb = (created.SizeBytes ?? 0) / 1024d / 1024d;
            var warning = created.Metadata?.SkippedFiles > 0
                ? $" Warning: skipped {created.Metadata.SkippedFiles} locked file(s); this backup is not restorable."
                : "";
            return new ActionResult(true,
                $"Backup created: {Path.GetFileName(created.Path)} ({mb:0.##} MB).{warning}", corr);
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

        await PruneBareMetalAsync(def, ct);
        _tracker.Update(id, current => current with { LastBackupAt = DateTimeOffset.UtcNow });
        return new ActionResult(true, "Pre-update backup: " + Path.GetFileName(created.Path), corr);
    }

    public async Task<List<BackupInfo>> ListAsync(string id, CancellationToken ct = default)
    {
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);
        return def.HostingMode == HostingMode.BareMetal
            ? ListBareMetal(def)
            : await ListHyperVAsync(def, ct);
    }

    public async Task<ActionResult> RestoreAsync(string id, string backupName, CancellationToken ct = default)
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
        var (probeOk, runningPid) = await _local.FindProcessAsync(def.GuestExePath, ct);
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
            return new ActionResult(false, "Backup is incomplete because files were skipped during creation; restore is disabled to prevent data loss.", corr);

        var zipPath = BackupSafety.ResolveUnderRoot(ServerBackupDir(def), safeName);
        var targets = GetSaveTargets(def, requireAnyExisting: false);
        ValidateArchiveEntries(zipPath, targets);

        var safety = await CreateBareMetalArchiveAsync(
            def,
            "Pre-restore safety copy",
            BackupKind.SafetyArchive,
            requireComplete: true,
            ct);
        if (!safety.Success)
            return new ActionResult(false, "Safety backup failed: " + (safety.Error ?? "unknown error") + ". Restore was not started.", corr);

        await PruneBareMetalAsync(def, ct);

        var swaps = targets.Select(t => new RestoreSwap
        {
            Target = t.Path,
            Stage = t.Path + ".gsc-restore-" + corr,
            Old = t.Path + ".gsc-old-" + corr,
            HadOriginal = Directory.Exists(t.Path)
        }).ToList();

        try
        {
            ExtractArchiveToStages(zipPath, targets, swaps, ct);

            foreach (var swap in swaps)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(swap.Target) ?? throw new InvalidOperationException("Save directory has no parent."));
                if (swap.HadOriginal)
                {
                    Directory.Move(swap.Target, swap.Old);
                    swap.OriginalMoved = true;
                }
                Directory.Move(swap.Stage, swap.Target);
                swap.StageMoved = true;
            }

            foreach (var swap in swaps)
                if (swap.OriginalMoved && Directory.Exists(swap.Old)) Directory.Delete(swap.Old, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore swap failed for {ServerId}; attempting rollback", def.Id);
            RollbackSwaps(swaps);
            CleanupStages(swaps);
            return new ActionResult(false, "Restore failed and rollback was attempted: " + ex.Message, corr);
        }

        CleanupStages(swaps);
        return new ActionResult(true,
            $"Restored from {safeName}. Safety copy: {Path.GetFileName(safety.Path)}", corr);
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
        var baseName = safety
            ? $"{def.Id}-prerestore-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.zip"
            : $"{def.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.zip";
        var zipPath = UniqueArchivePath(serverDir, baseName);
        var createdAt = DateTimeOffset.UtcNow;
        var fileCount = 0;
        var skipped = 0;

        try
        {
            await Task.Run(() =>
            {
                using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
                foreach (var target in targets)
                {
                    if (!Directory.Exists(target.Path)) continue;
                    foreach (var file in Directory.EnumerateFiles(target.Path, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        var rel = Path.GetRelativePath(target.Path, file);
                        var entryName = BackupSafety.NormalizeArchiveEntry(target.Prefix + "/" + rel.Replace('\\', '/'));
                        try
                        {
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                            using var output = entry.Open();
                            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                            input.CopyTo(output);
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
            }, ct);

            var metadata = new BackupMetadata(label, kind, createdAt, fileCount, skipped);
            await WriteBackupMetadataAsync(zipPath, metadata, ct);
            return new ArchiveCreateResult(true, zipPath, new FileInfo(zipPath).Length, metadata, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryDelete(zipPath);
            TryDelete(MetadataPath(zipPath));
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
                Restorable: meta?.SkippedFiles is not > 0));
        }

        return result.OrderByDescending(x => x.CreatedAt).ToList();
    }

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
        var normalKeep = EffectiveBackupRetention(def);
        foreach (var item in list.Where(x => x.Kind == BackupKind.Archive).Skip(normalKeep))
        {
            ct.ThrowIfCancellationRequested();
            DeleteArchiveByName(def, item.CheckpointName);
        }
        foreach (var item in list.Where(x => x.Kind == BackupKind.SafetyArchive).Skip(3))
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

    private void ValidateArchiveEntries(string zipPath, IReadOnlyList<SaveTarget> targets)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var normalized = BackupSafety.NormalizeArchiveEntry(entry.FullName);
            if (!targets.Any(t => normalized.Equals(t.Prefix, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(t.Prefix + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Backup contains unexpected path '{entry.FullName}'.");
        }
    }

    private void ExtractArchiveToStages(
        string zipPath,
        IReadOnlyList<SaveTarget> targets,
        IReadOnlyList<RestoreSwap> swaps,
        CancellationToken ct)
    {
        foreach (var swap in swaps)
        {
            if (Directory.Exists(swap.Stage)) Directory.Delete(swap.Stage, recursive: true);
            if (Directory.Exists(swap.Old)) Directory.Delete(swap.Old, recursive: true);
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

    private void RollbackSwaps(IReadOnlyList<RestoreSwap> swaps)
    {
        foreach (var swap in swaps.Reverse())
        {
            try
            {
                if (swap.StageMoved && Directory.Exists(swap.Target))
                    Directory.Delete(swap.Target, recursive: true);
                if (swap.OriginalMoved && Directory.Exists(swap.Old))
                    Directory.Move(swap.Old, swap.Target);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backup restore rollback failed for {Target}", swap.Target);
            }
        }
    }

    private static void CleanupStages(IEnumerable<RestoreSwap> swaps)
    {
        foreach (var swap in swaps)
        {
            TryDeleteDirectory(swap.Stage);
            if (!swap.OriginalMoved || swap.StageMoved) TryDeleteDirectory(swap.Old);
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
        return JsonSerializer.Deserialize<BackupMetadata>(File.ReadAllText(path), JsonOpts);
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
