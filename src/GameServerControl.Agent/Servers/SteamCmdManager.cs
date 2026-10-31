using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
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

    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var configured = _cfg["Agent:SteamCmdPath"];
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var onPath = WhichSteamCmd();
            if (onPath is not null) return onPath;

            var cached = Path.Combine(PrivateCacheDir, ExeName);
            if (File.Exists(cached)) return cached;

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
                await RunShellAsync($"tar -xzf \"{archivePath}\" -C \"{PrivateCacheDir}\"", ct);
                await RunShellAsync($"chmod +x \"{cached}\"", ct);
            }
            try { File.Delete(archivePath); } catch { }

            if (!File.Exists(cached))
                throw new FileNotFoundException("Extracted steamcmd archive but binary missing. Check " + PrivateCacheDir);
            _logger.LogInformation("steamcmd installed at {Path}", cached);
            return cached;
        }
        finally { _gate.Release(); }
    }

    private static string? WhichSteamCmd()
    {
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
    /// SteamCMD uses carriage-return progress updates while downloading. DataReceived/ReadLine
    /// only emits on newline, which made the dashboard look frozen until installation completed.
    /// This pump treats both CR and LF as record boundaries so progress reaches the browser live.
    /// </summary>
    private static async Task PumpOutputAsync(StreamReader reader, Action<string> onRecord, CancellationToken ct)
    {
        var buffer = new char[2048];
        var current = new StringBuilder(256);

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;

            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch is '\r' or '\n')
                {
                    if (current.Length > 0)
                    {
                        onRecord(current.ToString());
                        current.Clear();
                    }
                    continue;
                }
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            onRecord(current.ToString());
    }

    /// <summary>
    /// Run an anonymous SteamCMD app update. SteamCMD is unusual on its very first invocation:
    /// it may self-update and return 7, or even return 0 without ever reaching app_update. We
    /// therefore retry once when the completion marker was not observed. The caller still verifies
    /// the expected game executable on disk, which remains the final source of truth.
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
            if (attempt > 1)
                onLine($"Retrying SteamCMD install (attempt {attempt}/{maxAttempts})…", null);

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
            var stateGate = new object();
            var sawSuccess = false;
            var sawAppUpdateActivity = false;
            var sawHardError = false;
            string? lastRecord = null;
            var lastRecordAt = DateTime.MinValue;

            void HandleRecord(string raw)
            {
                var line = raw.Trim();
                if (line.Length == 0) return;

                int? pct = null;
                var emit = true;
                lock (stateGate)
                {
                    if (line.Contains($"Success! App '{appId}'", StringComparison.OrdinalIgnoreCase))
                        sawSuccess = true;

                    if (line.Contains("Update state", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("verifying", StringComparison.OrdinalIgnoreCase))
                        sawAppUpdateActivity = true;

                    if (line.Contains("ERROR!", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("No subscription", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Disk write failure", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Missing file permissions", StringComparison.OrdinalIgnoreCase))
                        sawHardError = true;

                    var m = progressRegex.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v))
                        pct = (int)Math.Clamp(v, 0, 100);

                    // SteamCMD can redraw an identical status several times a second. Keep the live
                    // feed useful without filling the bounded install log with exact duplicates.
                    var now = DateTime.UtcNow;
                    if (string.Equals(lastRecord, line, StringComparison.Ordinal) && now - lastRecordAt < TimeSpan.FromSeconds(1))
                        emit = false;
                    else
                    {
                        lastRecord = line;
                        lastRecordAt = now;
                    }
                }

                if (emit) onLine(line, pct);
            }

            if (!p.Start()) return (false, -1);
            var stdoutTask = PumpOutputAsync(p.StandardOutput, HandleRecord, ct);
            var stderrTask = PumpOutputAsync(p.StandardError, HandleRecord, ct);

            try
            {
                await p.WaitForExitAsync(ct);
                await Task.WhenAll(stdoutTask, stderrTask);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
                return (false, -2);
            }

            lastExitCode = p.ExitCode;
            bool success;
            bool hardError;
            bool activity;
            lock (stateGate)
            {
                success = sawSuccess;
                hardError = sawHardError;
                activity = sawAppUpdateActivity;
            }

            if (success)
            {
                if (lastExitCode != 0)
                    onLine($"SteamCMD reported success despite exit code {lastExitCode}; verifying files on disk…", null);
                return (true, lastExitCode);
            }

            if (hardError)
                return (false, lastExitCode);

            if (attempt < maxAttempts && (lastExitCode == 7 || !activity))
            {
                var reason = lastExitCode == 7
                    ? "SteamCMD bootstrap exited with code 7"
                    : "SteamCMD exited before a completed app_update was observed";
                onLine(reason + "; retrying once after bootstrap initialization…", null);
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { return (false, -2); }
                continue;
            }

            // On the final attempt allow exit 0 through to the caller's on-disk executable check.
            // Some SteamCMD builds omit the Success! line even though the depot operation completed.
            return (lastExitCode == 0, lastExitCode);
        }

        return (false, lastExitCode);
    }
}
