using System.Diagnostics;

namespace GameServerControl.Agent.Hyperv;

/// <summary>
/// Bare-metal counterpart to <see cref="GuestProcessService"/>. Runs everything directly
/// on the agent host instead of dispatching through PowerShell Direct into a VM guest.
/// </summary>
public sealed class LocalProcessService
{
    private readonly ILogger<LocalProcessService> _logger;
    public LocalProcessService(ILogger<LocalProcessService> logger) { _logger = logger; }

    public bool HasScheduledTaskSupport => OperatingSystem.IsWindows();

    public Task<(bool Ok, int? Pid, string Error)> StartProcessAsync(string exePath, string[] args, string workingDir, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(exePath))
                return Task.FromResult<(bool, int?, string)>((false, null, $"EXE not found: {exePath}"));

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(exePath) ?? "" : workingDir,
                UseShellExecute = true,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            if (p is null) return Task.FromResult<(bool, int?, string)>((false, null, "Process.Start returned null"));
            return Task.FromResult<(bool, int?, string)>((true, p.Id, ""));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartProcess failed for {Exe}", exePath);
            return Task.FromResult<(bool, int?, string)>((false, null, ex.Message));
        }
    }

    public Task<(bool Ok, int? Pid)> FindProcessAsync(string exePath, CancellationToken ct = default)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            var candidates = Process.GetProcessesByName(name);
            if (candidates.Length == 0)
                return Task.FromResult<(bool, int?)>((true, null));

            var unreadable = new List<Process>();
            foreach (var p in candidates)
            {
                try
                {
                    if (PathsEqual(p.MainModule?.FileName, exePath))
                        return Task.FromResult<(bool, int?)>((true, p.Id));
                }
                catch
                {
                    unreadable.Add(p);
                }
            }

            // If there is exactly one process with that leaf name and Windows denied MainModule
            // access, accepting it is the least surprising fallback. With two or more candidates,
            // guessing could stop or report the wrong game-server instance, so return Unknown.
            if (candidates.Length == 1 && unreadable.Count == 1)
                return Task.FromResult<(bool, int?)>((true, unreadable[0].Id));

            if (candidates.Length > 1)
                _logger.LogWarning("Ambiguous process lookup for {Exe}: found {Count} processes named {Name} and no exact path match.", exePath, candidates.Length, name);

            return Task.FromResult<(bool, int?)>((candidates.Length == 1, null));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindProcess failed for {Exe}", exePath);
            return Task.FromResult<(bool, int?)>((false, null));
        }
    }

    public async Task<bool> StopProcessAsync(string exePath, int? pid, bool force, CancellationToken ct = default)
    {
        try
        {
            Process? p = null;
            if (pid is int id)
            {
                try
                {
                    var candidate = Process.GetProcessById(id);
                    try
                    {
                        var actual = candidate.MainModule?.FileName;
                        if (actual is null || PathsEqual(actual, exePath)) p = candidate;
                        else _logger.LogWarning("Refusing to stop pid {Pid}: executable is {Actual}, expected {Expected}.", id, actual, exePath);
                    }
                    catch
                    {
                        // Same-service/admin deployments can occasionally fail MainModule access.
                        // A PID supplied by our own tracker is still safer than a name-only lookup.
                        p = candidate;
                    }
                }
                catch { /* process may already be gone */ }
            }

            if (p is null)
            {
                var (ok, foundPid) = await FindProcessAsync(exePath, ct);
                if (!ok)
                {
                    _logger.LogWarning("Refusing name-only stop for {Exe}: process lookup is ambiguous or unavailable.", exePath);
                    return false;
                }
                if (foundPid is null) return true;
                try { p = Process.GetProcessById(foundPid.Value); }
                catch { return true; }
            }

            if (!force)
            {
                try { p.CloseMainWindow(); } catch { /* no UI */ }
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline && !p.HasExited)
                    await Task.Delay(500, ct);
                if (p.HasExited) return true;
            }

            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Kill failed for pid {Pid}", p.Id); return false; }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StopProcess failed for {Exe}", exePath);
            return false;
        }
    }

    public async Task<bool> StartScheduledTaskAsync(string taskName, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return false;
        var (ok, output) = await RunCommandAsync($"schtasks /run /tn \"{taskName}\"", "", TimeSpan.FromSeconds(15), ct);
        if (!ok) _logger.LogWarning("schtasks /run for {Task} failed: {Out}", taskName, output);
        return ok;
    }

    public async Task<bool> EndScheduledTaskAsync(string taskName, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return true;
        var (ok, _) = await RunCommandAsync($"schtasks /end /tn \"{taskName}\"", "", TimeSpan.FromSeconds(15), ct);
        return ok;
    }

    public async Task<bool?> GetAutostartAsync(string taskName, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return null;
        var (ok, output) = await RunCommandAsync($"schtasks /query /tn \"{taskName}\" /fo LIST /v", "", TimeSpan.FromSeconds(10), ct);
        if (!ok || string.IsNullOrWhiteSpace(output)) return null;
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Scheduled Task State", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var val = line.Substring(colon + 1).Trim();
                if (val.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return true;
                if (val.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return null;
    }

    public async Task<bool> SetAutostartAsync(string taskName, bool enabled, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return true;
        var flag = enabled ? "/enable" : "/disable";
        var (ok, _) = await RunCommandAsync($"schtasks /change /tn \"{taskName}\" {flag}", "", TimeSpan.FromSeconds(15), ct);
        return ok;
    }

    public Task KillByNamesAsync(IEnumerable<string> exeNames, CancellationToken ct = default)
    {
        foreach (var raw in exeNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bare = Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrEmpty(bare)) continue;
            try
            {
                foreach (var p in Process.GetProcessesByName(bare))
                {
                    try { p.Kill(entireProcessTree: true); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Kill pid {Pid} ({Name}) failed", p.Id, bare); }
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Kill by name {Name} failed", bare); }
        }
        return Task.CompletedTask;
    }

    public async Task<(bool Ok, string Output)> RunCommandAsync(string commandLine, string workingDir, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + commandLine;
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(commandLine);
            }
            using var p = Process.Start(psi);
            if (p is null) return (false, "Process.Start returned null");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "TIMEOUT");
            }
            var stdout = await outTask;
            var stderr = await errTask;
            var combined = stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n[stderr]\n" + stderr);
            return (p.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunCommand failed: {Cmd}", commandLine);
            return (false, ex.Message);
        }
    }

    private static bool PathsEqual(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
    }
}
