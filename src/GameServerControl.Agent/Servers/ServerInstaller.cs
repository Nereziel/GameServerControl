using System.Collections.Concurrent;
using GameServerControl.Agent.Hubs;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Runs SteamCMD against a given install path, then registers the supplied ServerDef on success.
/// Progress is streamed over SignalR ("installProgress" event); jobs are in-memory only so a
/// restart mid-install loses the in-flight job.
/// </summary>
public sealed class ServerInstaller
{
    private readonly SteamCmdManager _steamCmd;
    private readonly ServerStore _store;
    private readonly IHubContext<StatusHub> _hub;
    private readonly ILogger<ServerInstaller> _logger;

    private readonly ConcurrentDictionary<string, InstallProgress> _jobs = new();

    public ServerInstaller(SteamCmdManager steamCmd, ServerStore store,
        IHubContext<StatusHub> hub, ILogger<ServerInstaller> logger)
    {
        _steamCmd = steamCmd;
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    public InstallProgress? Get(string jobId) => _jobs.TryGetValue(jobId, out var p) ? p : null;

    public string StartJob(InstallServerRequest req)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        _jobs[jobId] = new InstallProgress(jobId, "queued", "Queued.", null, false, false);
        _ = Task.Run(() => RunAsync(jobId, req, CancellationToken.None));
        return jobId;
    }

    private async Task RunAsync(string jobId, InstallServerRequest req, CancellationToken ct)
    {
        try
        {
            await BroadcastAsync(new InstallProgress(jobId, "steamcmd", "Resolving SteamCMD…", 0, false, false));

            var (ok, exitCode) = await _steamCmd.RunAppUpdateAsync(
                req.InstallPath,
                req.SteamAppId,
                (line, pct) =>
                {
                    _ = BroadcastAsync(new InstallProgress(jobId, "steamcmd", line, pct, false, false));
                },
                ct);

            if (!ok)
            {
                await BroadcastAsync(new InstallProgress(jobId, "failed",
                    $"SteamCMD exited with code {exitCode}. Check disk space, Steam rate limiting, and the SteamCMD output above.",
                    null, true, false));
                return;
            }

            // SteamCMD exit 0 only means the depot operation completed. Verify the preset points
            // at a file that really exists before claiming the server is ready. This catches stale
            // executable names immediately (for example when a game changes its dedicated binary).
            var expectedExe = req.ServerDef.GuestExePath;
            if (!File.Exists(expectedExe))
            {
                await BroadcastAsync(new InstallProgress(jobId, "failed",
                    $"SteamCMD finished, but the expected server executable was not found: {expectedExe}. " +
                    "The game files remain installed; update the preset/executable path and use discovery or add the server manually.",
                    null, true, false));
                return;
            }

            if (!string.Equals(req.ServerDef.SteamAppId, req.SteamAppId, StringComparison.Ordinal))
            {
                await BroadcastAsync(new InstallProgress(jobId, "failed",
                    $"Steam App ID mismatch: install request used {req.SteamAppId}, but ServerDef contains {req.ServerDef.SteamAppId ?? "(none)"}.",
                    null, true, false));
                return;
            }

            await BroadcastAsync(new InstallProgress(jobId, "register", "Registering server…", 99, false, false));
            try
            {
                _store.Add(req.ServerDef);
            }
            catch (Exception ex)
            {
                await BroadcastAsync(new InstallProgress(jobId, "failed",
                    $"SteamCMD finished but registration failed: {ex.Message}. The game files ARE installed at {req.InstallPath}; you can add the server manually.",
                    null, true, false));
                return;
            }

            await BroadcastAsync(new InstallProgress(jobId, "done",
                $"Done. Server '{req.ServerDef.Name}' is ready to start.",
                100, true, true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install job {Id} crashed", jobId);
            await BroadcastAsync(new InstallProgress(jobId, "failed", "Exception: " + ex.Message, null, true, false));
        }
    }

    private async Task BroadcastAsync(InstallProgress p)
    {
        _jobs[p.JobId] = p;
        await _hub.Clients.All.SendAsync("installProgress", p);
    }
}
