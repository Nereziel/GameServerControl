using System.Diagnostics;

namespace GameServerControl.Agent.Notifications;

/// <summary>
/// Starts the repository's platform installer in a detached child process so the installer
/// can stop/replace/restart this agent safely. The installer preserves appsettings.json,
/// servers.json and tokens.json and downloads the latest published release assets.
/// </summary>
public sealed class SelfUpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly IConfiguration _cfg;
    private readonly ILogger<SelfUpdateService> _logger;
    private int _starting;

    public SelfUpdateService(IConfiguration cfg, ILogger<SelfUpdateService> logger)
    {
        _cfg = cfg;
        _logger = logger;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("gsc-self-updater/1.0");
    }

    public bool IsStarting => Volatile.Read(ref _starting) != 0;

    public async Task<(bool Ok, string Message)> StartLatestAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            return (false, "An update is already being started.");

        try
        {
            var repo = _cfg["Agent:Repo"] ?? "Nereziel/GameServerControl";
            var installRoot = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName
                              ?? AppContext.BaseDirectory;

            if (OperatingSystem.IsWindows())
            {
                var url = $"https://raw.githubusercontent.com/{repo}/main/deploy/windows/install.ps1";
                var script = await DownloadScriptAsync(url, ".ps1", ct);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };
                psi.Environment["GSC_REPO"] = repo;
                psi.Environment["GSC_INSTALL"] = installRoot;
                var p = Process.Start(psi);
                if (p is null) throw new InvalidOperationException("Could not start PowerShell updater.");
                _logger.LogWarning("Self-update launched as PID {Pid}; repository {Repo}, root {Root}", p.Id, repo, installRoot);
                return (true, "Update started. The agent will briefly go offline while the latest release is installed. Running GameServerControl clients may be closed during the update.");
            }

            if (OperatingSystem.IsLinux())
            {
                var url = $"https://raw.githubusercontent.com/{repo}/main/deploy/linux/install.sh";
                var script = await DownloadScriptAsync(url, ".sh", ct);
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"\"{script}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                };
                psi.Environment["GSC_REPO"] = repo;
                psi.Environment["GSC_INSTALL"] = installRoot;
                var p = Process.Start(psi);
                if (p is null) throw new InvalidOperationException("Could not start Linux updater.");
                _logger.LogWarning("Self-update launched as PID {Pid}; repository {Repo}, root {Root}", p.Id, repo, installRoot);
                return (true, "Update started. The agent will briefly go offline while the latest release is installed.");
            }

            return (false, "Self-update is currently supported on Windows and Linux only.");
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _starting, 0);
            _logger.LogError(ex, "Could not start self-update.");
            return (false, ex.Message);
        }
    }

    private static async Task<string> DownloadScriptAsync(string url, string extension, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Downloaded updater script is empty.");
        var path = Path.Combine(Path.GetTempPath(), $"gsc-update-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, text, ct);
        return path;
    }
}
