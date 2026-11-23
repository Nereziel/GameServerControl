using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Config;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Agent.Notifications;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Servers;

public sealed class ServerOrchestrator
{
    private readonly ProcessMetricsSampler _metrics = new();
    private readonly ConcurrentDictionary<string, bool> _stopRequested = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _lifecycleLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerUpdateConsole _updateConsole;
    private readonly HypervService _hv;
    private readonly GuestProcessService _guest;
    private readonly LocalProcessService _local;
    private readonly ServerRegistry _registry;
    private readonly StatusTracker _tracker;
    private readonly IHubContext<StatusHub> _hub;
    private readonly DiscordNotifier _discord;
    private readonly ServerDiagnostics _diagnostics;
    private readonly SteamCmdManager _steamCmd;
    private readonly DragonwildsConfig _dragonwildsConfig;
    private readonly BackupService _backups;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ServerOrchestrator> _logger;

    public ServerOrchestrator(
        HypervService hv,
        GuestProcessService guest,
        LocalProcessService local,
        ServerRegistry registry,
        StatusTracker tracker,
        IHubContext<StatusHub> hub,
        DiscordNotifier discord,
        ServerDiagnostics diagnostics,
        ServerUpdateConsole updateConsole,
        SteamCmdManager steamCmd,
        DragonwildsConfig dragonwildsConfig,
        BackupService backups,
        IConfiguration cfg,
        ILogger<ServerOrchestrator> logger)
    {
        _hv = hv;
        _guest = guest;
        _local = local;
        _registry = registry;
        _tracker = tracker;
        _hub = hub;
        _discord = discord;
        _diagnostics = diagnostics;
        _updateConsole = updateConsole;
        _steamCmd = steamCmd;
        _dragonwildsConfig = dragonwildsConfig;
        _backups = backups;
        _cfg = cfg;
        _logger = logger;
    }

    private GuestCredential GetCred(string? credId)
    {
        var id = string.IsNullOrEmpty(credId) ? "default" : credId;
        var section = _cfg.GetSection($"Agent:GuestCredentials:{id}");
        return new GuestCredential
        {
            Username = section["Username"] ?? "",
            Password = section["Password"] ?? ""
        };
    }

    public async Task<ServerStatus> RefreshStatusAsync(string id, CancellationToken ct = default)
    {
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);

        VmState vmState;
        ProcessState procState;
        int? pid = null;

        if (def.HostingMode == HostingMode.BareMetal)
        {
            vmState = VmState.NotApplicable;
            var (ok, foundPid) = await _local.FindProcessAsync(def, ct);
            procState = !ok ? ProcessState.Unknown : (foundPid is null ? ProcessState.NotRunning : ProcessState.Running);
            pid = foundPid;
        }
        else if (!OperatingSystem.IsWindows())
        {
            vmState = VmState.Unknown;
            procState = ProcessState.Unknown;
        }
        else
        {
            vmState = await _hv.GetStateAsync(def.VmName, ct);
            procState = ProcessState.Unknown;

            if (vmState == VmState.Running)
            {
                var cred = GetCred(def.GuestCredentialId);
                try
                {
                    var (ok, foundPid) = await _guest.FindProcessAsync(def.VmName, cred, def.GuestExePath, ct);
                    if (ok) { pid = foundPid; procState = foundPid is null ? ProcessState.NotRunning : ProcessState.Running; }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FindProcess failed for {Vm}", def.VmName);
                }
            }
            else
            {
                procState = ProcessState.NotRunning;
            }
        }

        (double? cpuPct, long? memMB) = def.HostingMode == HostingMode.BareMetal && pid is int pp
            ? _metrics.Sample(def, pp)
            : (null, null);

        var metadata = await BuildMetadataAsync(def, ct);
        var prevState = _tracker.Get(id).ProcessState;

        var status = _tracker.Update(id, cur => cur with
        {
            VmState = vmState,
            ProcessState = procState,
            PidInGuest = pid,
            CpuPercent = cpuPct,
            MemoryMB = memMB,
            LastError = prevState == ProcessState.Running && procState == ProcessState.NotRunning
                ? "Unexpected process exit detected; check the game logs."
                : prevState == ProcessState.NotRunning && procState == ProcessState.Running
                    ? null : cur.LastError,
            Metadata = metadata
        });
        await BroadcastAsync(status);

        if (prevState == ProcessState.Running && procState == ProcessState.NotRunning)
        {
            var wasStop = _stopRequested.TryRemove(id, out _);
            if (!wasStop)
            {
                _diagnostics.Record(id, "UnexpectedStop", "Error",
                    "Process stopped without an Agent stop request. Cause unknown; review game logs.");
                _ = _discord.SendAsync(def, DiscordNotifier.Kind.Warn,
                    "Server stopped unexpectedly",
                    "Process exited without a stop request. Check WebUI Diagnostics.", CancellationToken.None);
            }
        }
        if (prevState == ProcessState.NotRunning && procState == ProcessState.Running)
            _diagnostics.Record(id, "RecoveryObserved", "Success",
                "Process running again. The Agent cannot independently verify who restarted it.");
        return status;
    }

    private async Task<Dictionary<string, string>?> BuildMetadataAsync(ServerDef def, CancellationToken ct)
    {
        if (def.GameType != GameType.Windrose) return null;
        var candidates = new[]
        {
            Path.Combine(def.GuestWorkingDir, "R5", "ServerDescription.json"),
            Path.Combine(def.GuestWorkingDir, "ServerDescription.json"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var section = (JsonNode.Parse(text) as JsonObject)?["ServerDescription_Persistent"] as JsonObject;
            var dict = new Dictionary<string, string>();
            if (section?.TryGetPropertyValue("InviteCode", out var inv) == true && inv is not null)
                dict["InviteCode"] = inv.ToString();
            if (section?.TryGetPropertyValue("ServerName", out var sn) == true && sn is not null)
                dict["ServerName"] = sn.ToString();
            return dict.Count > 0 ? dict : null;
        }
        catch { return null; }
    }

    private readonly ConcurrentDictionary<string, ServerStatus> _lastBroadcastStatus = new(StringComparer.OrdinalIgnoreCase);

    private async Task BroadcastAsync(ServerStatus s)
    {
        // For an unchanged server (especially one stopped for hours), avoid
        // broadcasting an identical status on every five-second poll.
        if (_lastBroadcastStatus.TryGetValue(s.Id, out var previous) &&
            !ServerStatusChangeDetector.HasChanged(previous, s)) return;
        _lastBroadcastStatus[s.Id] = s;
        await _hub.Clients.All.SendAsync("statusChanged", s);
    }

    private async Task LogAsync(string id, string source, string text)
    {
        var line = new LogLine(id, DateTimeOffset.UtcNow, source, text);
        await _hub.Clients.All.SendAsync("logLine", line);
        _logger.LogInformation("[{Id}] {Src}: {Text}", id, source, text);
    }

    private async Task<ActionResult> WithLifecycleLockAsync(
        string id, Func<Task<ActionResult>> operation, CancellationToken ct)
    {
        var gate = _lifecycleLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            return new ActionResult(false,
                "Another Start, Stop, Restart or Update operation is already running for this server.",
                Guid.NewGuid().ToString("n"));
        try { return await operation(); }
        finally { gate.Release(); }
    }

    // Auto-update settings are independent: a Restart does not implicitly
    // inherit AutoUpdateOnStart. This also prevents a duplicate SteamCMD run.
    public static bool ShouldAutoUpdate(ServerDef def, bool restart)
        => restart ? def.AutoUpdateOnRestart : def.AutoUpdateOnStart;

    public Task<ActionResult> StartAsync(string id, CancellationToken ct = default)
        => WithLifecycleLockAsync(id, () => StartCoreAsync(id, fromRestart: false, ct), ct);

    private async Task<ActionResult> StartCoreAsync(string id, bool fromRestart, CancellationToken ct)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        var profile = GameProfiles.FindFor(def);
        if (def.HostingMode == HostingMode.Vm && profile is not null && !profile.Capabilities.HyperV)
            return new ActionResult(false, $"{profile.Label} is supported only in BareMetal hosting mode.", corr);

        await LogAsync(id, "orch", $"Start requested ({def.HostingMode})");
        if (ShouldAutoUpdate(def, fromRestart))
        {
            // Only when the process is definitely stopped. Never modify files
            // underneath a running dedicated server.
            if (def.HostingMode != HostingMode.BareMetal)
                return StartFailure(id, corr, "Auto-update is supported only on BareMetal servers.");
            var (checkedOk, existingPid) = await _local.FindProcessAsync(def, ct);
            if (!checkedOk)
                return StartFailure(id, corr, "Auto-update cancelled: could not confirm game process state.");
            if (existingPid is null)
            {
                var updated = await UpdateCoreAsync(id, ct);
                if (!updated.Success)
                    return StartFailure(id, corr, "Auto-update failed; server not started: " + updated.Message);
            }
            // If Start was requested while a game was already running,
            // StartBareMetalAsync will report Already running without updating.
        }
        // SteamCMD may rewrite DedicatedServer.ini during auto-update.
        // Validate/recover protected Dragonwilds settings after all file updates.
        if (def.SteamAppId == "4019830")
        {
            var readinessError = await _dragonwildsConfig.ValidateForStartAsync(def, ct);
            if (!string.IsNullOrWhiteSpace(readinessError))
                return StartFailure(id, corr, readinessError);
        }
        return def.HostingMode == HostingMode.BareMetal
            ? await StartBareMetalAsync(def, corr, ct)
            : await StartVmAsync(def, corr, ct);
    }

    private ActionResult StartFailure(string id, string correlation, string message)
    {
        _tracker.Update(id, status => status with { LastError = message });
        _diagnostics.Record(id, "Start", "Error", "Server start failed. Review the Last error field.");
        var server = _registry.Get(id);
        if (server is not null)
            _ = _discord.SendAsync(server, DiscordNotifier.Kind.Warn,
                "Server start failed", "Check WebUI Diagnostics for details.", CancellationToken.None);
        _logger.LogWarning("Server start failed for {ServerId}: {Message}", id, message);
        return new ActionResult(false, message, correlation);
    }

    private static string ExplainLaunchError(string? error)
    {
        var detail = string.IsNullOrWhiteSpace(error) ? "Unknown process error." : error;
        var hint = detail.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("access", StringComparison.OrdinalIgnoreCase)
            ? "Check the Runner account's permissions on the executable, working directory and save folders."
            : detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
              detail.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
            ? "Check the executable and working directory in server configuration."
            : "Check the server executable path, permissions and game logs.";
        return "[GSC-START-PROCESS] " + detail + " " + hint;
    }

    private async Task<ActionResult> StartBareMetalAsync(ServerDef def, string corr, CancellationToken ct)
    {
        var (alreadyOk, existingPid) = await _local.FindProcessAsync(def, ct);
        if (alreadyOk && existingPid is int pidAlready)
        {
            _tracker.Update(def.Id, s => s with { ProcessState = ProcessState.Running, PidInGuest = pidAlready });
            await RefreshStatusAsync(def.Id, ct);
            return new ActionResult(true, $"Already running (pid={pidAlready})", corr);
        }

        await LogAsync(def.Id, "orch", "Launching " + def.GuestExePath);
        var (started, pid, err) = await _local.StartProcessAsync(def, ct);
        if (!started) return StartFailure(def.Id, corr, ExplainLaunchError(err));

        _tracker.Update(def.Id, s => s with { ProcessState = ProcessState.Running, PidInGuest = pid, StartedAt = DateTimeOffset.UtcNow, LastError = null });
        await RefreshStatusAsync(def.Id, ct);
        _diagnostics.Record(def.Id, "Start", "Success", "Server started via Agent.");
        _ = _discord.SendAsync(def, DiscordNotifier.Kind.Up, "▶ Server started", $"{def.Name} is up (pid={pid}).", ct);
        return new ActionResult(true, $"Started (pid={pid})", corr);
    }

    private async Task<ActionResult> StartVmAsync(ServerDef def, string corr, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return new ActionResult(false, "Vm hosting mode requires Windows + Hyper-V. Use BareMetal on Linux.", corr);

        var state = await _hv.GetStateAsync(def.VmName, ct);
        if (state != VmState.Running)
        {
            await LogAsync(def.Id, "orch", "Starting VM " + def.VmName);
            if (!await _hv.StartVmAsync(def.VmName, ct))
                return StartFailure(def.Id, corr,
                    "[GSC-START-VM] Hyper-V could not start the VM. Check Hyper-V service and VM configuration.");

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                state = await _hv.GetStateAsync(def.VmName, ct);
                if (state == VmState.Running) break;
                await Task.Delay(2000, ct);
            }
            if (state != VmState.Running) return StartFailure(def.Id, corr,
                "[GSC-START-VM-TIMEOUT] VM did not reach Running state within three minutes. Check Hyper-V events.");
        }

        var cred = GetCred(def.GuestCredentialId);
        var reachable = false;
        var reachDeadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < reachDeadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (ok, _) = await _guest.FindProcessAsync(def.VmName, cred, def.GuestExePath, ct);
                if (ok) { reachable = true; break; }
            }
            catch { }
            await Task.Delay(3000, ct);
        }
        if (!reachable) return StartFailure(def.Id, corr,
            "[GSC-START-GUEST] VM is running but PowerShell Direct is unavailable. Check guest credentials and integration services.");

        await LogAsync(def.Id, "orch", "Launching " + def.GuestExePath);
        var (started, pid, err) = await _guest.StartProcessAsync(def.VmName, cred, def.GuestExePath, def.StartArgs, def.GuestWorkingDir, ct);
        if (!started) return new ActionResult(false, "Process launch failed: " + err, corr);

        _tracker.Update(def.Id, s => s with { ProcessState = ProcessState.Running, PidInGuest = pid, StartedAt = DateTimeOffset.UtcNow, LastError = null });
        await RefreshStatusAsync(def.Id, ct);
        _ = _discord.SendAsync(def, DiscordNotifier.Kind.Up, "▶ Server started", $"{def.Name} is up (pid={pid}).", ct);
        return new ActionResult(true, $"Started (pid={pid})", corr);
    }

    public Task<ActionResult> StopAsync(string id, bool stopVm, bool force, CancellationToken ct = default)
        => WithLifecycleLockAsync(id, () => StopCoreAsync(id, stopVm, force, ct), ct);

    private async Task<ActionResult> StopCoreAsync(string id, bool stopVm, bool force, CancellationToken ct)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        await LogAsync(id, "orch", $"Stop requested (mode={def.HostingMode}, stopVm={stopVm}, force={force})");
        _stopRequested[id] = true;

        if (def.HostingMode == HostingMode.BareMetal)
        {
            var pid = _tracker.Get(id).PidInGuest;
            var stoppedGracefully = false;
            if (def.GameType == GameType.Minecraft && !force)
            {
                var (sent, _) = await _local.SendConsoleInputAsync(def, "stop", ct);
                if (sent)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        var (found, runningPid) = await _local.FindProcessAsync(def, ct);
                        if (found && runningPid is null) { stoppedGracefully = true; break; }
                        await Task.Delay(500, ct);
                    }
                }
            }
            if (!stoppedGracefully)
                await _local.StopProcessAsync(def, pid, force, ct);

            if (def.StopProcessNames is { Length: > 0 } extras)
            {
                await LogAsync(id, "orch", "Killing additional process names: " + string.Join(", ", extras));
                await _local.KillByNamesAsync(def, extras, ct);
            }

            _tracker.Update(id, s => s with { ProcessState = ProcessState.NotRunning, PidInGuest = null });
            await RefreshStatusAsync(id, ct);
            _stopRequested.TryRemove(id, out _);
            _diagnostics.Record(id, "Stop", "Info", "Server stopped by operator.");
            _ = _discord.SendAsync(def, DiscordNotifier.Kind.Down, "■ Server stopped", $"{def.Name} stopped via dashboard.", ct);
            return new ActionResult(true, "Stopped", corr);
        }

        var cred = GetCred(def.GuestCredentialId);
        var pidVm = _tracker.Get(id).PidInGuest;
        try
        {
            await _guest.StopProcessAsync(def.VmName, cred, def.GuestExePath, pidVm, force, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Stop process error (continuing)"); }

        if (stopVm)
        {
            await LogAsync(id, "orch", "Stopping VM " + def.VmName);
            await _hv.StopVmAsync(def.VmName, force, ct);
        }

        _tracker.Update(id, s => s with { ProcessState = ProcessState.NotRunning, PidInGuest = null });
        await RefreshStatusAsync(id, ct);
        _stopRequested.TryRemove(id, out _);
        _diagnostics.Record(id, "Stop", "Info", "Server stopped by operator.");
        _ = _discord.SendAsync(def, DiscordNotifier.Kind.Down, "■ Server stopped", $"{def.Name} stopped via dashboard.", ct);
        return new ActionResult(true, "Stopped", corr);
    }

    public Task<ActionResult> RestartAsync(string id, CancellationToken ct = default)
        => WithLifecycleLockAsync(id, () => RestartCoreAsync(id, ct), ct);

    private async Task<ActionResult> RestartCoreAsync(string id, CancellationToken ct)
    {
        _diagnostics.Record(id, "ManualRestart", "Info", "Dashboard/API restart requested.");
        var stop = await StopCoreAsync(id, stopVm: false, force: false, ct);
        if (!stop.Success) return stop;
        await Task.Delay(2000, ct);
        return await StartCoreAsync(id, fromRestart: true, ct);
    }

    // Compatibility methods for existing ServerOrchestrator consumers.
    // All backup operations (including the legacy entry points) MUST use the
    // same validation, metadata, retention, safety snapshots and staged restore
    // rules as the Agent's HTTP endpoints. Do not reintroduce ZIP IO here.
    public Task<ActionResult> BackupAsync(string id, CancellationToken ct = default)
        => _backups.CreateAsync(id, ct);

    public Task<List<BackupInfo>> ListBackupsAsync(string id, CancellationToken ct = default)
        => _backups.ListAsync(id, ct);

    public Task<ActionResult> RestoreBackupAsync(
        string id, string backupName, CancellationToken ct = default)
        => _backups.RestoreAsync(id, backupName, ct);

    public Task<bool> DeleteBackupAsync(
        string id, string backupName, CancellationToken ct = default)
        => _backups.DeleteAsync(id, backupName, ct);

    public Task<ActionResult> UpdateAsync(string id, CancellationToken ct = default)
        => WithLifecycleLockAsync(id, () => UpdateCoreAsync(id, ct), ct);

    private async Task<ActionResult> UpdateCoreAsync(string id, CancellationToken ct)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        _updateConsole.Begin(id, "Checking server state before SteamCMD update…");
        ActionResult Fail(string reason)
        {
            _updateConsole.End(id, false, reason);
            return new ActionResult(false, reason, corr);
        }

        try
        {
            if (string.IsNullOrWhiteSpace(def.SteamAppId))
                return Fail("No SteamAppId set; cannot update via SteamCMD.");

            if (def.HostingMode == HostingMode.BareMetal)
            {
                var (ok, pid) = await _local.FindProcessAsync(def, ct);
                if (!ok) return Fail("Cannot verify process state. Update was cancelled.");
                if (pid is not null)
                    return Fail("Stop the game server before updating it. No files were modified.");
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                    return Fail("Hyper-V guest updates require Windows.");
                if (await _hv.GetStateAsync(def.VmName, ct) != VmState.Running)
                    return Fail("VM not running.");
                var cred = GetCred(def.GuestCredentialId);
                var (ok, pid) = await _guest.FindProcessAsync(def.VmName, cred, def.GuestExePath, ct);
                if (!ok || pid is not null)
                    return Fail("The guest game process must be confirmed stopped before updating.");
            }

            _updateConsole.Write(id, "Creating required pre-update safety backup…");
            var preUpdate = await _backups.CreatePreUpdateAsync(id, ct);
            if (!preUpdate.Success)
                return Fail("Update cancelled: pre-update backup failed: " + preUpdate.Message);
            _updateConsole.Write(id, "Pre-update backup completed.");
            await LogAsync(id, "backup", preUpdate.Message);

            if (def.HostingMode == HostingMode.BareMetal)
            {
                await LogAsync(id, "orch", $"SteamCMD update app {def.SteamAppId} in {def.GuestWorkingDir}");
                _updateConsole.Write(id, "SteamCMD updating and validating game files…");
                var (ok, exitCode) = await _steamCmd.RunAppUpdateAsync(
                    def.GuestWorkingDir,
                    def.SteamAppId,
                    (line, percent) =>
                    {
                        _updateConsole.Write(id, line, percent);
                        _ = LogAsync(id, "steamcmd", line);
                    },
                    ct);
                var result = ok ? "Update finished" : $"Update failed (SteamCMD exit {exitCode})";
                _updateConsole.End(id, ok, result);
                _diagnostics.Record(id, "Update", ok ? "Success" : "Error",
                    ok ? "SteamCMD update completed." : "SteamCMD update failed.");
                return new ActionResult(ok, result, corr);
            }

            var guestCred = GetCred(def.GuestCredentialId);
            var inGuestCmd = $"steamcmd +login anonymous +force_install_dir \"{def.GuestWorkingDir}\" +app_update {def.SteamAppId} validate +quit";
            _updateConsole.Write(id, "SteamCMD running inside Hyper-V guest (output appears when complete)…");
            var (okGuest, outputGuest) = await _guest.RunCommandInGuestAsync(def.VmName,
                guestCred, inGuestCmd, def.GuestWorkingDir, TimeSpan.FromMinutes(30), ct);
            // Guest command output is returned in one batch rather than streamed.
            foreach (var line in outputGuest.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(240))
                _updateConsole.Write(id, line);
            await LogAsync(id, "steamcmd", outputGuest);
            _updateConsole.End(id, okGuest, okGuest ? "Update finished" : "Update failed");
            return new ActionResult(okGuest, okGuest ? "Update finished" : "Update failed", corr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _updateConsole.End(id, false, "Game update cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Game update failed for {ServerId}", id);
            return Fail("Update failed. Review the update console and Agent log.");
        }
    }

    public async Task<ActionResult> ApplyConfigAsync(string id, CancellationToken ct = default)
        => await RestartAsync(id, ct);
}
