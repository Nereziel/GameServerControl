using System.Diagnostics;

namespace GameServerControl.Agent.Notifications;

public sealed record AgentUpdateLaunch(bool Started, string Message);
public sealed record AgentUpdatePreflight(
    bool Supported,
    bool Ready,
    string Platform,
    string? HelperPath,
    bool HelperPresent,
    string Message);

/// <summary>
/// Starts an updater process outside the Agent directory so the Windows service can safely
/// stop and replace its own files. The helper is created by the Windows installer at the
/// install root (for example C:\GameServerControl\Update-GameServerControl.ps1).
/// </summary>
public sealed class AgentUpdateService
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<AgentUpdateService> _logger;
    private int _launching;

    public AgentUpdateService(IConfiguration cfg, ILogger<AgentUpdateService> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public AgentUpdatePreflight GetPreflight()
    {
        if (!OperatingSystem.IsWindows())
        {
            var platform = OperatingSystem.IsLinux() ? "Linux" : OperatingSystem.IsMacOS() ? "macOS" : "Unknown";
            return new AgentUpdatePreflight(
                Supported: false,
                Ready: false,
                Platform: platform,
                HelperPath: null,
                HelperPresent: false,
                Message: "Web-triggered self-update is supported on Windows only. Use the Linux release installer on Linux.");
        }

        var agentDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installRoot = Directory.GetParent(agentDir)?.FullName;
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return new AgentUpdatePreflight(
                Supported: true,
                Ready: false,
                Platform: "Windows",
                HelperPath: null,
                HelperPresent: false,
                Message: "Could not determine the GameServerControl install root.");
        }

        var helper = Path.Combine(installRoot, "Update-GameServerControl.ps1");
        var present = File.Exists(helper);
        return new AgentUpdatePreflight(
            Supported: true,
            Ready: present,
            Platform: "Windows",
            HelperPath: helper,
            HelperPresent: present,
            Message: present
                ? "Updater helper is ready. The Agent can perform a self-update."
                : $"Updater helper is missing: {helper}. Re-run the Windows installer once to create it.");
    }

    public AgentUpdateLaunch StartLatestUpdate()
    {
        var preflight = GetPreflight();
        if (!preflight.Ready)
            return new(false, preflight.Message);

        if (Interlocked.CompareExchange(ref _launching, 1, 0) != 0)
            return new(false, "An update launch is already in progress.");

        try
        {
            var helper = preflight.HelperPath!;
            var installRoot = Path.GetDirectoryName(helper);
            if (string.IsNullOrWhiteSpace(installRoot))
                return Fail("Could not determine the GameServerControl install root.");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{helper}\" -Silent",
                WorkingDirectory = installRoot,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var repo = _cfg["Agent:Repo"];
            if (!string.IsNullOrWhiteSpace(repo)) psi.Environment["GSC_REPO"] = repo;
            psi.Environment["GSC_INSTALL"] = installRoot;

            var process = Process.Start(psi);
            if (process is null) return Fail("Failed to start the updater process.");

            _logger.LogWarning("Self-update helper launched as PID {Pid}. The Agent service will restart shortly.", process.Id);
            _ = Task.Run(async () =>
            {
                try { await process.WaitForExitAsync(); }
                catch { }
                finally
                {
                    process.Dispose();
                    Interlocked.Exchange(ref _launching, 0);
                }
            });

            return new(true, "Updater started. The Agent will go offline briefly and restart automatically.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch self-update helper");
            return Fail(ex.Message);
        }
    }

    private AgentUpdateLaunch Fail(string message)
    {
        Interlocked.Exchange(ref _launching, 0);
        return new(false, message);
    }
}
