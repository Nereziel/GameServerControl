using System.Collections.Concurrent;
using System.IO.Compression;
using GameServerControl.Agent.Hubs;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Asynchronous dedicated-server installer. Jobs continue when the browser closes; callers can
/// re-attach through GET /api/servers/install/{jobId}. SteamCMD installs are serialized because
/// sharing one SteamCMD cache between concurrent bootstrap/update processes is unreliable.
/// </summary>
public sealed class ServerInstaller
{
    private const int MaxLogLines = 500;
    private const string TerrariaServerUrl =
        "https://terraria.org/api/download/pc-dedicated-server/terraria-server-1458.zip";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(20) };

    private readonly SteamCmdManager _steamCmd;
    private readonly ServerStore _store;
    private readonly IHubContext<StatusHub> _hub;
    private readonly ILogger<ServerInstaller> _logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly ConcurrentDictionary<string, JobState> _jobs = new();

    private sealed class JobState
    {
        public object Gate { get; } = new();
        public List<string> Lines { get; } = new();
        public InstallProgress Snapshot { get; set; }

        public JobState(InstallProgress snapshot) => Snapshot = snapshot;
    }

    public ServerInstaller(SteamCmdManager steamCmd, ServerStore store,
        IHubContext<StatusHub> hub, ILogger<ServerInstaller> logger)
    {
        _steamCmd = steamCmd;
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    public InstallProgress? Get(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return null;
        lock (state.Gate)
            return state.Snapshot with { LogLines = state.Lines.ToArray() };
    }

    public string StartJob(InstallServerRequest req)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        var now = DateTimeOffset.UtcNow;
        var initial = new InstallProgress(
            jobId, "queued", "Queued for background installation.", 0, false, false,
            Array.Empty<string>(), now, now, req.ServerDef.Name, GameKey(req.SteamAppId), req.InstallPath);
        var state = new JobState(initial);
        state.Lines.Add("Queued for background installation.");
        _jobs[jobId] = state;
        _ = Task.Run(() => RunAsync(jobId, req, CancellationToken.None));
        return jobId;
    }

    private async Task RunAsync(string jobId, InstallServerRequest req, CancellationToken ct)
    {
        var gateHeld = false;
        try
        {
            if (!string.Equals(req.ServerDef.SteamAppId, req.SteamAppId, StringComparison.Ordinal))
            {
                await PublishAsync(jobId, "failed",
                    $"Steam App ID mismatch: request used {req.SteamAppId}, ServerDef contains {req.ServerDef.SteamAppId ?? "(none)"}.",
                    null, true, false);
                return;
            }

            await PublishAsync(jobId, "queued", "Waiting for the install worker…", 0, false, false);
            await _installGate.WaitAsync(ct);
            gateHeld = true;

            await PublishAsync(jobId, "steamcmd", "Resolving SteamCMD…", 1, false, false);
            var (steamOk, exitCode) = await _steamCmd.RunAppUpdateAsync(
                req.InstallPath,
                req.SteamAppId,
                (line, pct) => _ = PublishAsync(jobId, "steamcmd", line, pct, false, false),
                ct);

            var expectedExe = req.ServerDef.GuestExePath;
            var exeExists = File.Exists(expectedExe);

            // Terraria's server is distributed both with the main game and as a free official
            // Re-Logic dedicated-server archive. Steam anonymous access has changed over time and
            // can leave only Steam metadata behind. Fall back to the official archive whenever the
            // SteamCMD result is incomplete, rather than forcing the operator to install manually.
            if (req.SteamAppId == "105600" && (!steamOk || !exeExists) && OperatingSystem.IsWindows())
            {
                var reason = !steamOk
                    ? $"SteamCMD did not complete Terraria (exit {exitCode})."
                    : $"SteamCMD completed but {expectedExe} was not present.";
                await PublishAsync(jobId, "download", reason + " Falling back to the official Terraria dedicated-server package…", 5, false, false);

                var fallbackOk = await InstallTerrariaOfficialAsync(jobId, req.InstallPath, ct);
                exeExists = File.Exists(expectedExe);
                if (!fallbackOk || !exeExists)
                {
                    await PublishAsync(jobId, "failed",
                        $"Terraria fallback finished but the expected executable is still missing: {expectedExe}",
                        null, true, false);
                    return;
                }
            }
            else if (!steamOk)
            {
                await PublishAsync(jobId, "failed",
                    $"SteamCMD exited with code {exitCode}. Review the install log above for the actual Steam error.",
                    null, true, false);
                return;
            }

            await PublishAsync(jobId, "verify", "Verifying installed server files…", 96, false, false);
            if (!File.Exists(expectedExe))
            {
                await PublishAsync(jobId, "failed",
                    $"Installer finished, but the expected server executable was not found: {expectedExe}. " +
                    "The partial files were left in place for troubleshooting; the server was not registered.",
                    null, true, false);
                return;
            }

            await PublishAsync(jobId, "register", "Registering server with GameServerControl…", 99, false, false);
            try
            {
                _store.Add(req.ServerDef);
            }
            catch (Exception ex)
            {
                await PublishAsync(jobId, "failed",
                    $"Files are installed, but registration failed: {ex.Message}. Install path: {req.InstallPath}",
                    null, true, false);
                return;
            }

            await PublishAsync(jobId, "done",
                $"Done. Server '{req.ServerDef.Name}' is installed and registered.",
                100, true, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install job {Id} crashed", jobId);
            await PublishAsync(jobId, "failed", "Exception: " + ex.Message, null, true, false);
        }
        finally
        {
            if (gateHeld) _installGate.Release();
        }
    }

    private async Task<bool> InstallTerrariaOfficialAsync(string jobId, string installPath, CancellationToken ct)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gsc-terraria-" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempRoot, "terraria-server.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(extractPath);

        try
        {
            await PublishAsync(jobId, "download", "Downloading official Terraria Server 1.4.5.8 from terraria.org…", 10, false, false);
            using var resp = await _http.GetAsync(TerrariaServerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength;
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[128 * 1024];
                long readTotal = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    readTotal += read;
                    if (total is > 0)
                    {
                        var pct = 10 + (int)Math.Clamp(readTotal * 55 / total.Value, 0, 55);
                        await PublishAsync(jobId, "download",
                            $"Downloading official package… {readTotal / 1024 / 1024} / {total.Value / 1024 / 1024} MB",
                            pct, false, false, addToLog: false);
                    }
                }
            }

            await PublishAsync(jobId, "download", "Extracting official Terraria dedicated-server package…", 70, false, false);
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);

            var serverExe = Directory.EnumerateFiles(extractPath, "TerrariaServer.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (serverExe is null)
            {
                await PublishAsync(jobId, "download", "Official archive did not contain TerrariaServer.exe.", null, false, false);
                return false;
            }

            var sourceDir = Path.GetDirectoryName(serverExe)!;
            Directory.CreateDirectory(installPath);
            CopyDirectory(sourceDir, installPath);
            await PublishAsync(jobId, "download", "Official Terraria Windows server files installed.", 94, false, false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terraria official fallback failed");
            await PublishAsync(jobId, "download", "Official Terraria fallback failed: " + ex.Message, null, false, false);
            return false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, dir)));

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private async Task PublishAsync(string jobId, string phase, string line, int? pct, bool finished, bool success, bool addToLog = true)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;

        InstallProgress snapshot;
        lock (state.Gate)
        {
            if (addToLog && !string.IsNullOrWhiteSpace(line))
            {
                if (state.Lines.Count == 0 || !string.Equals(state.Lines[^1], line, StringComparison.Ordinal))
                    state.Lines.Add(line);
                if (state.Lines.Count > MaxLogLines)
                    state.Lines.RemoveRange(0, state.Lines.Count - MaxLogLines);
            }

            var previous = state.Snapshot;
            state.Snapshot = previous with
            {
                Phase = phase,
                Line = line,
                PercentHint = pct ?? previous.PercentHint,
                Finished = finished,
                Success = success,
                UpdatedAt = DateTimeOffset.UtcNow,
                LogLines = state.Lines.ToArray()
            };
            snapshot = state.Snapshot;
        }

        try { await _hub.Clients.All.SendAsync("installProgress", snapshot); }
        catch (Exception ex) { _logger.LogDebug(ex, "Install progress SignalR broadcast failed for {JobId}", jobId); }
    }

    private static string GameKey(string appId) => appId switch
    {
        "896660" => "valheim",
        "4019830" => "dragonwilds",
        "2394010" => "palworld",
        "1690800" => "satisfactory",
        "2430930" => "ark-asa",
        "376030" => "ark-se",
        "258550" => "rust",
        "1829350" => "vrising",
        "443030" => "conan",
        "403240" => "squad",
        "3017310" => "soulmask",
        "294420" => "7dtd",
        "105600" => "terraria",
        "343050" => "dst",
        "380870" => "zomboid",
        _ => "steam"
    };
}
