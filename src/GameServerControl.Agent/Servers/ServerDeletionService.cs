using GameServerControl.Agent.Logs;
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
    private readonly LocalProcessService _local;
    private readonly LogTailService _tail;
    private readonly MaintenanceScheduler _scheduler;
    private readonly IConfiguration _cfg;
    private readonly IHostEnvironment _env;
    private readonly ILogger<ServerDeletionService> _logger;

    public ServerDeletionService(
        ServerRegistry registry,
        ServerStore store,
        ServerOrchestrator orchestrator,
        LocalProcessService local,
        LogTailService tail,
        MaintenanceScheduler scheduler,
        IConfiguration cfg,
        IHostEnvironment env,
        ILogger<ServerDeletionService> logger)
    {
        _registry = registry;
        _store = store;
        _orchestrator = orchestrator;
        _local = local;
        _tail = tail;
        _scheduler = scheduler;
        _cfg = cfg;
        _env = env;
        _logger = logger;
    }

    public async Task<DeleteServerResult> DeleteCompletelyAsync(
        string id,
        DeleteServerRequest request,
        CancellationToken ct = default)
    {
        var def = _registry.Get(id);
        if (def is null)
            return Fail("not_found", "Server not found.");

        if (!string.Equals(request.Confirmation?.Trim(), def.Name, StringComparison.Ordinal))
            return Fail("confirmation_mismatch", $"Type the exact server name '{def.Name}' to confirm deletion.");

        if (def.HostingMode != HostingMode.BareMetal)
            return Fail("unsupported_hosting_mode", "Complete file deletion is only supported for servers running on this machine. Hyper-V guest files are not deleted from the host.");

        if (!TryValidateDeleteRoot(def, out var deleteRoot, out var validationError))
            return Fail("unsafe_path", validationError!);

        // Stop log tail first so it cannot keep the log file open during deletion.
        _tail.Stop(id);

        // Stop the game process, but do not stop a VM (BareMetal is enforced above).
        var stop = await _orchestrator.StopAsync(id, stopVm: false, force: false, ct);
        if (!stop.Success)
            return Fail("stop_failed", "Server could not be stopped: " + stop.Message);

        // Verify independently that the executable process is really gone before deleting files.
        var (processCheckOk, remainingPid) = await _local.FindProcessAsync(def.GuestExePath, ct);
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

            var backup = await _orchestrator.BackupAsync(id, ct);
            if (!backup.Success)
                return Fail("backup_failed", "Final backup failed, so deletion was cancelled: " + backup.Message);
            backupCreated = true;
        }

        // Remove GameServerControl maintenance tasks before the definition disappears.
        try { await _scheduler.ApplyAsync(id, null, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove maintenance tasks for {Id}; continuing with server deletion", id);
        }

        try
        {
            if (Directory.Exists(deleteRoot))
            {
                if (ContainsReparsePoint(deleteRoot))
                    return Fail("reparse_point", "The server folder contains a symbolic link/junction/reparse point. Refusing recursive deletion for safety.");

                NormalizeAttributes(deleteRoot);
                Directory.Delete(deleteRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Permanent deletion failed for {Id} at {Path}", id, deleteRoot);
            return Fail("delete_failed", "Could not delete the complete server folder. The server remains registered. " + ex.Message);
        }

        if (!_store.Delete(id))
        {
            _logger.LogError("Server files for {Id} were deleted but the server definition could not be removed", id);
            return new DeleteServerResult(false, "definition_delete_failed",
                "Server files were deleted, but its GameServerControl definition could not be removed. Reload the server list and remove the stale definition manually.",
                deleteRoot, backupCreated);
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

            // Require the configured executable to live inside the directory being deleted.
            var exe = Path.GetFullPath(def.GuestExePath);
            if (!IsSameOrChild(exe, deleteRoot) || PathsEqual(exe, deleteRoot))
            {
                error = "The configured server executable is not inside the server folder. Refusing recursive deletion.";
                return false;
            }

            var protectedPaths = new List<string?>
            {
                _env.ContentRootPath,
                AppContext.BaseDirectory,
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
            if (IsSameOrChild(fullBackupRoot, deleteRoot))
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
            foreach (var dir in Directory.EnumerateDirectories(current))
            {
                var attrs = File.GetAttributes(dir);
                if ((attrs & FileAttributes.ReparsePoint) != 0) return true;
                pending.Push(dir);
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
