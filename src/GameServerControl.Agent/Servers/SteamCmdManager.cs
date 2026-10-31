using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Resolves steamcmd in order: Agent:SteamCmdPath config, PATH, private cache under the
/// agent dir. Downloads the official Valve archive if none of those exist.
/// </summary>
public sealed class SteamCmdManager
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IConfiguration _cfg;
    private readonly ILogger<SteamCmdManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SteamCmdManager(IConfiguration cfg, ILogger<SteamCmdManager> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    private string ExeName => OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh";

    private string DistributionUrl => OperatingSystem.IsWindows()
        ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
        : "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";

    private string PrivateCacheDir => Path.Combine(AppContext.BaseDirectory, "SteamCMD");

    /// <summary>Find or fetch steamcmd, returning the absolute path to the binary.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // 1) Operator override
            var configured = _cfg["Agent:SteamCmdPath"];
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            // 2) PATH lookup — covers winget/apt installs
            var onPath = WhichSteamCmd();
            if (onPath is not null) return onPath;

            // 3) Private cache
            var cached = Path.Combine(PrivateCacheDir, ExeName);
            if (File.Exists(cached)) return cached;

            // 4) Download fresh
            _logger.LogInformation("steamcmd not found — downloading from {Url}", DistributionUrl);
            Directory.CreateDirectory(PrivateCacheDir);
            var archivePath = Path.Combine(PrivateCacheDir, OperatingSystem.IsWindows() ? "steamcmd.zip" : "steamcmd.tar.gz");
            using (var resp = await _http.GetAsync(DistributionUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(archivePath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (OperatingSystem.IsWindows())
            {
                ZipFile.ExtractToDirectory(archivePath, PrivateCacheDir, overwriteFiles: true);
            }
            else
            {
                // Use tar via shell — extracts steamcmd.sh + linux32/steamcmd into place.
                await RunShellAsync($"tar -xzf \"{archivePath}\" -C \"{PrivateCacheDir}\"", ct);
                await RunShellAsync($"chmod +x \"{cached}\"", ct);
            }
            try { File.Delete(archivePath); } catch { /* not critical */ }

            if (!File.Exists(cached))
                throw new FileNotFoundException("Extracted steamcmd archive but binary missing. Check " + PrivateCacheDir);
            _logger.LogInformation("steamcmd installed at {Path}", cached);
            return cached;
        }
        finally { _gate.Release(); }
    }

    private static string? WhichSteamCmd()
    {
        // Plain "where"/"which" style PATH lookup with both candidate exe names.
        var candidates = OperatingSystem.IsWindows() ? new[] { "steamcmd.exe", "steamcmd" } : new[] { "steamcmd.sh", "steamcmd" };
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in candidates)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static async Task RunShellAsync(string cmd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"{cmd.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
    }

    /// <summary>
    /// Runs <c>+app_update {appId} validate</c> and streams stdout/stderr through
    /// <paramref name="onLine"/>. A freshly unpacked SteamCMD often self-updates on its
    /// first run and exits with code 7 before the requested app command really executes.
    /// That code is therefore retried once. A SteamCMD Success! marker is also accepted
    /// even if the bootstrapper returns a non-zero exit code; ServerInstaller performs a
    /// second on-disk executable check before registering the server.
    /// </summary>
    public async Task<(bool Ok, int ExitCode)> RunAppUpdateAsync(
        string installDir,
        string appId,
        Action<string, int?> onLine,
        CancellationToken ct)
    {
        var exe = await EnsureAsync(ct);
        Directory.CreateDirectory(installDir);

        const int maxAttempts = 2;
        var lastExitCode = -1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var args = $"+force_install_dir \"{installDir}\" +login anonymous +app_update {appId} validate +quit";
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            };

            using var p = new Process { StartInfo = psi };
            var progressRegex = new Regex(@"progress:\s*([\d.]+)", RegexOptions.IgnoreCase);
            var sawSuccess = false;

            void HandleLine(string line)
            {
                if (line.Contains($"Success! App '{appId}'", StringComparison.OrdinalIgnoreCase))
                    sawSuccess = true;

                int? pct = null;
                var m = progressRegex.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    pct = (int)Math.Clamp(v, 0, 100);
                onLine(line, pct);
            }

            p.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) HandleLine(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) HandleLine(e.Data);
            };

            if (!p.Start()) return (false, -1);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
                // WaitForExit() after the async wait lets redirected output handlers drain before
                // we inspect sawSuccess.
                p.WaitForExit();
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { /* swallow */ }
                return (false, -2);
            }

            lastExitCode = p.ExitCode;

            if (sawSuccess)
            {
                if (lastExitCode != 0)
                    onLine($"SteamCMD reported success despite exit code {lastExitCode}; verifying installed executable on disk…", null);
                return (true, lastExitCode);
            }

            if (lastExitCode == 0)
                return (true, 0);

            if (lastExitCode == 7 && attempt < maxAttempts)
            {
                onLine("SteamCMD first-run/bootstrap returned exit code 7; retrying the install once…", null);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return (false, -2); }
                continue;
            }

            return (false, lastExitCode);
        }

        return (false, lastExitCode);
    }
}
