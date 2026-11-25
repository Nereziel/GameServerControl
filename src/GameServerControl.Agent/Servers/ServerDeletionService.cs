using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Logs;
using GameServerControl.Agent.Networking;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

public sealed record DeleteServerRequest(string Confirmation, bool CreateFinalBackup = true);

public sealed record DeleteServerResult(
    bool Success,
    string Code,
    string Message,
    string? DeletedPath = null,
    bool BackupCreated = false);

/// <summary>
/// Permanently removes a BareMetal game server directory after stopping it and
/// performing conservative path-safety checks. The server definition is removed
/// only after the filesystem deletion succeeds.
/// </summary>
public sealed class ServerDeletionService
{
    private readonly ServerRegistry _registry;
    private readonly ServerStore _store;
    private readonly ServerOrchestrator _orchestrator;
    private readonly BackupService _backups;
    private readonly LocalProcessService _local;
    private readonly LogTailService _tail;
    private readonly MaintenanceScheduler _scheduler;
    private readonly ServerLifecycleGate _lifecycle;
    private readonly IWindowsRunnerServiceManager _runners;
    private readonly PublicRelayManager _relay;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ServerDeletionService> _logger;

    public ServerDeletionService(
        ServerRegistry registry,
        ServerStore store,
        ServerOrchestrator orchestrator,
        BackupService backups,
        LocalProcessService local,
        LogTailService tail,
        MaintenanceScheduler scheduler,
        ServerLifecycleGate lifecycle,
        IWindowsRunnerServiceManager runners,
        PublicRelayManager relay,
        IConfiguration cfg,
        IHostEnvironment env,
        ILogger<ServerDeletionService> logger)
    {
        _registry = registry;
        _store = store;
        _orchestrator = orchestrator;
        _backups = backups;
        _local = local;
        _tail = tail;
        _scheduler = scheduler;
        _lifecycle = lifecycle;
        _runners = runners;
        _relay = relay;
        _cfg = cfg;
        _env = env;
        _logger = logger;
    }

    public Task<DeleteServerResult> DeleteCompletelyAsync(
        string id, DeleteServerRequest request, CancellationToken ct = default)
        => _lifecycle.RunExclusiveAsync(id, () => DeleteCoreAsync(id, request, ct),
            () => Fail("busy", "Another lifecycle operation is running for this server."), ct);

    private async Task<DeleteServerResult> DeleteCoreAsync(
        string id, DeleteServerRequest request, CancellationToken ct)
    {
        var def = _registry.Get(id);
        if (def is null)
            return Fail("not_found", "Server not found.");

        if (!string.Equals(request.Confirmation, def.Name, StringComparison.Ordinal))
            return Fail("confirmation_mismatch", $"Type the exact server name '{def.Name}' to confirm deletion.");

        if (def.HostingMode != HostingMode.BareMetal)
            return Fail("unsupported_hosting_mode", "Complete file deletion is only supported for servers running on this machine. Hyper-V guest files are not deleted from the host.");

        if (!TryValidateDeleteRoot(def, out var deleteRoot, out var validationError))
            return Fail("unsafe_path", validationError!);

        if (!Directory.Exists(deleteRoot) || ContainsReparsePoint(deleteRoot))
            return Fail("unsafe_path", "The server folder must exist and must not contain symbolic links or reparse points.");
        if (!BackupLocationIsSafe(deleteRoot, out var rootError))
            return Fail("unsafe_backup_path", rootError!);

        // Stop log tail first so it cannot keep the log file open during deletion.
        _tail.Stop(id);

        // Stop the game process, but do not stop a VM (BareMetal is enforced above).
        var stop = await _orchestrator.StopCoreAsync(id, stopVm: false, force: false, ct);
        if (!stop.Success)
            return Fail("stop_failed", "Server could not be stopped: " + stop.Message);

        // Verify independently that the executable process is really gone before deleting files.
        var (processCheckOk, remainingPid) = await _local.FindProcessAsync(def, ct);
        if (!processCheckOk)
            return Fail("stop_unverified", "GameServerControl could not verify that the server process stopped. No files were deleted.");
        if (remainingPid is not null)
            return Fail("still_running", $"Server process is still running (PID {remainingPid}). No files were deleted.");

        var backupCreated = false;
        if (request.CreateFinalBackup)
        {
            if (def.SaveDirs is null || def.SaveDirs.Length == 0)
                return Fail("backup_unavailable", "Final backup was requested, but this server has no save folders configured. Disable final backup or configure save folders first.");

            if (!BackupLocationIsSafe(deleteRoot, out var backupError))
                return Fail("unsafe_backup_path", backupError!);

            var backup = await _backups.CreatePreUpdateAsync(id, ct);
            if (!backup.Success)
                return Fail("backup_failed", "Final backup failed, so deletion was cancelled: " + backup.Message);
            backupCreated = true;
        }

        // Prevent Windows Runner autostart and remove its ACL/service state
        // before deleting files. Keep the deprovision fence until removal ends.
        var deprovisioned = false;
        try
        {
            if (OperatingSystem.IsWindows())
                await _scheduler.ApplyAsync(id, null, ct);
            if (_runners.Supports(def))
            {
                var cleanup = await _runners.DeprovisionAsync(def, ct);
                if (!cleanup.Ok) return Fail("runner_cleanup_failed", cleanup.Error);
                deprovisioned = true;
            }

            // A second check closes the window while the backup was made.
            if (!TryValidateDeleteRoot(def, out var checkedRoot, out var error) ||
                !PathsEqual(checkedRoot, deleteRoot) || !Directory.Exists(deleteRoot) ||
                ContainsReparsePoint(deleteRoot))
                return Fail("unsafe_path", error ?? "Server folder changed during deletion.");
            ct.ThrowIfCancellationRequested();

            // Once destructive work starts, finish local bookkeeping even if
            // the HTTP client disconnects. A registry mutation cannot overlap
            // this operation for the same server through the lifecycle gate.
            NormalizeAttributes(deleteRoot);
            Directory.Delete(deleteRoot, recursive: true);
            if (!_store.Delete(id))
                return new DeleteServerResult(false, "definition_delete_failed",
                    "Server files were deleted but the definition could not be removed.", deleteRoot, backupCreated);
            await _relay.RemoveServerAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Permanent deletion failed for {Id}", id);
            return Fail("delete_failed", "Deletion did not complete. Check the Agent log and server folder before retrying.");
        }
        finally
        {
            if (deprovisioned) _runners.CompleteDeprovision(id);
        }

        return new DeleteServerResult(true, "deleted",
            backupCreated
                ? "Server stopped, final backup created, server folder deleted, and definition removed."
                : "Server stopped, server folder deleted, and definition removed.",
            deleteRoot, backupCreated);
    }

    private bool TryValidateDeleteRoot(ServerDef def, out string deleteRoot, out string? error)
    {
        deleteRoot = "";
        error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(def.GuestWorkingDir) || !Path.IsPathRooted(def.GuestWorkingDir))
            {
                error = "Server folder must be an absolute path.";
                return false;
            }

            deleteRoot = TrimEndingSeparator(Path.GetFullPath(def.GuestWorkingDir));
            var volumeRoot = TrimEndingSeparator(Path.GetPathRoot(deleteRoot) ?? "");
            if (string.IsNullOrWhiteSpace(volumeRoot) || PathsEqual(deleteRoot, volumeRoot))
            {
                error = "Refusing to delete a drive/filesystem root.";
                return false;
            }

            // Complete deletion is opt-in and limited to a dedicated local root.
            var allowed = _cfg["Agent:PermanentDeleteRoot"];
            if (string.IsNullOrWhiteSpace(allowed) || !Path.IsPathFullyQualified(allowed) ||
                !PathContainment.IsWithinRoot(allowed, deleteRoot))
            {
                error = "Permanent deletion requires a dedicated Agent:PermanentDeleteRoot containing this server folder.";
                return false;
            }
            // Reject links in every ancestor, not just the final directory.
            for (var ancestor = new DirectoryInfo(deleteRoot); ancestor is not null; ancestor = ancestor.Parent)
            {
                if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "Server folder has a symbolic-link or reparse-point ancestor.";
                    return false;
                }
            }
            foreach (var other in _registry.All.Where(x => x.HostingMode == HostingMode.BareMetal && !string.Equals(x.Id, def.Id, StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(other.GuestWorkingDir)) continue;
                if (IsSameOrChild(other.GuestWorkingDir, deleteRoot) || IsSameOrChild(deleteRoot, other.GuestWorkingDir))
                {
                    error = "Server folder overlaps another registered server.";
                    return false;
                }
            }
            // Require the configured executable to live inside the directory being deleted.
            var exe = Path.GetFullPath(def.GuestExePath);
            var javaServer = def.GameType == GameType.Minecraft && def.GameProfileKey != "minecraft-bedrock";
            if (javaServer ? !File.Exists(Path.Combine(deleteRoot, "server.jar")) : (!IsSameOrChild(exe, deleteRoot) || PathsEqual(exe, deleteRoot)))
            {
                error = "The configured server executable is not inside the server folder. Refusing recursive deletion.";
                return false;
            }

            var protectedPaths = new List<string?>
            {
                _env.ContentRootPath,
                AppContext.BaseDirectory,
                _cfg["Agent:DataDir"],
                _cfg["Agent:OffsiteBackupRoot"],
                OperatingSystem.IsLinux() ? "/etc" : null,
                OperatingSystem.IsLinux() ? "/usr" : null,
                OperatingSystem.IsLinux() ? "/boot" : null,
                OperatingSystem.IsLinux() ? "/proc" : null,
                OperatingSystem.IsLinux() ? "/sys" : null,
                OperatingSystem.IsLinux() ? "/dev" : null,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            };

            foreach (var protectedPath in protectedPaths.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                var p = TrimEndingSeparator(Path.GetFullPath(protectedPath!));
                // Reject deleting a protected directory, anything inside it, or any parent that contains it.
                if (IsSameOrChild(deleteRoot, p) || IsSameOrChild(p, deleteRoot))
                {
                    error = $"Refusing to delete protected/system path '{deleteRoot}'.";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Invalid server folder: " + ex.Message;
            return false;
        }
    }

    private bool BackupLocationIsSafe(string deleteRoot, out string? error)
    {
        error = null;
        try
        {
            var configured = _cfg["Agent:BackupRoot"];
            var backupRoot = configured ?? (OperatingSystem.IsWindows()
                ? @"C:\GameServerControl\Backups"
                : "/var/lib/gameservercontrol/backups");
            var fullBackupRoot = TrimEndingSeparator(Path.GetFullPath(backupRoot));
            if (IsSameOrChild(fullBackupRoot, deleteRoot) || IsSameOrChild(deleteRoot, fullBackupRoot))
            {
                error = "BackupRoot is inside the server folder. A final backup would be deleted with the server; move BackupRoot outside the server folder first.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "Could not validate BackupRoot: " + ex.Message;
            return false;
        }
    }

    private static bool ContainsReparsePoint(string root)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return true;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
                if ((attrs & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        return false;
    }

    private static void NormalizeAttributes(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch { /* deletion below will report the real failure */ }
        }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        candidate = TrimEndingSeparator(Path.GetFullPath(candidate));
        parent = TrimEndingSeparator(Path.GetFullPath(parent));
        if (PathsEqual(candidate, parent)) return true;
        return candidate.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison)
            || candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(TrimEndingSeparator(a), TrimEndingSeparator(b), PathComparison);

    private static string TrimEndingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static DeleteServerResult Fail(string code, string message)
        => new(false, code, message);
}
