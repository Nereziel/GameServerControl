using System.Collections.Concurrent;
using System.Diagnostics;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Hyperv;

public sealed record LocalConsoleState(
    bool Supported,
    bool InputAvailable,
    string Message,
    LogLine[] Lines);

/// <summary>
/// Bare-metal counterpart to <see cref="GuestProcessService"/>. Runs everything directly
/// on the agent host instead of dispatching through PowerShell Direct into a VM guest.
/// For profiles explicitly marked as <see cref="GameConsoleMode.Stdin"/>, the process is
/// launched with redirected stdin/stdout/stderr and its console is kept attached to the Agent.
/// </summary>
public sealed class LocalProcessService
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(8);
    private const int MaxConsoleLines = 400;
    private const int MaxConsoleChars = 200_000;
    private const int MaxConsoleLineChars = 16_384;

    private readonly ILogger<LocalProcessService> _logger;
    private readonly ServerRegistry _registry;
    private readonly IHubContext<StatusHub> _hub;
    private readonly ConcurrentDictionary<string, ConsoleSession> _consoleSessions = new(StringComparer.OrdinalIgnoreCase);

    public LocalProcessService(
        ILogger<LocalProcessService> logger,
        ServerRegistry registry,
        IHubContext<StatusHub> hub)
    {
        _logger = logger;
        _registry = registry;
        _hub = hub;
    }

    public bool HasScheduledTaskSupport => OperatingSystem.IsWindows();

    public Task<(bool Ok, int? Pid, string Error)> StartProcessAsync(string exePath, string[] args, string workingDir, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(exePath))
                return Task.FromResult<(bool, int?, string)>((false, null, $"EXE not found: {exePath}"));

            var def = FindServerForExe(exePath);
            var interactive = def is not null
                && def.HostingMode == HostingMode.BareMetal
                && string.IsNullOrWhiteSpace(def.ScheduledTaskName)
                && GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(exePath) ?? "" : workingDir,
                UseShellExecute = !interactive,
                RedirectStandardInput = interactive,
                RedirectStandardOutput = interactive,
                RedirectStandardError = interactive,
                CreateNoWindow = interactive,
                WindowStyle = interactive ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Minimized
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            if (p is null) return Task.FromResult<(bool, int?, string)>((false, null, "Process.Start returned null"));

            if (interactive && def is not null)
                AttachInteractiveConsole(def, p);

            return Task.FromResult<(bool, int?, string)>((true, p.Id, ""));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartProcess failed for {Exe}", exePath);
            return Task.FromResult<(bool, int?, string)>((false, null, ex.Message));
        }
    }

    private void AttachInteractiveConsole(ServerDef def, Process process)
    {
        var session = new ConsoleSession(def.Id, process);
        _consoleSessions[def.Id] = session;

        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) _ = RecordAndBroadcastAsync(session, "stdout", e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _ = RecordAndBroadcastAsync(session, "stderr", e.Data);
        };
        process.Exited += (_, _) =>
        {
            session.MarkExited();
            var code = "?";
            try { code = process.ExitCode.ToString(); } catch { }
            _ = RecordAndBroadcastAsync(session, "process", $"[process exited with code {code}]");
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _ = RecordAndBroadcastAsync(session, "process", $"[interactive console attached — pid {process.Id}]");
    }

    public LocalConsoleState GetConsoleState(ServerDef def)
    {
        var supported = GameProfiles.FindFor(def)?.Capabilities.ConsoleMode == GameConsoleMode.Stdin;
        if (!supported)
            return new LocalConsoleState(false, false, "This game profile does not use a stdin console.", Array.Empty<LogLine>());
        if (def.HostingMode != HostingMode.BareMetal)
            return new LocalConsoleState(true, false, "Interactive stdin is currently available only for servers running on this machine.", Array.Empty<LogLine>());
        if (!string.IsNullOrWhiteSpace(def.ScheduledTaskName))
            return new LocalConsoleState(true, false, "This server starts through Task Scheduler, so GameServerControl cannot attach its stdin. Remove the scheduled task launcher to use the interactive console.", Array.Empty<LogLine>());

        if (!_consoleSessions.TryGetValue(def.Id, out var session))
            return new LocalConsoleState(true, false, "Console input will attach after this server is started from GameServerControl. If it is already running, restart it from this panel.", Array.Empty<LogLine>());

        var lines = session.Snapshot();
        if (!session.InputAvailable)
            return new LocalConsoleState(true, false, "The attached process is no longer accepting console input.", lines);

        return new LocalConsoleState(true, true, "Interactive console attached.", lines);
    }

    public async Task<(bool Ok, string Error)> SendConsoleInputAsync(ServerDef def, string command, CancellationToken ct = default)
    {
        if (GameProfiles.FindFor(def)?.Capabilities.ConsoleMode != GameConsoleMode.Stdin)
            return (false, "This game profile does not support stdin console input.");
        if (def.HostingMode != HostingMode.BareMetal)
            return (false, "Interactive stdin is not available for Hyper-V guest processes yet.");
        if (!_consoleSessions.TryGetValue(def.Id, out var session) || !session.InputAvailable)
            return (false, "No attached console session. Start or restart this server from GameServerControl first.");

        command = command.Trim();
        if (command.Length == 0) return (false, "Command is empty.");
        if (command.Length > 4096) return (false, "Command is too long.");
        if (command.Contains('\r') || command.Contains('\n')) return (false, "Send one command at a time.");

        await session.InputGate.WaitAsync(ct);
        try
        {
            if (!session.InputAvailable)
                return (false, "The console session closed before the command could be sent.");
            await session.Process.StandardInput.WriteLineAsync(command);
            await session.Process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Console input failed for {ServerId}", def.Id);
            return (false, ex.Message);
        }
        finally
        {
            session.InputGate.Release();
        }

        await RecordAndBroadcastAsync(session, "stdin", "> " + command);
        return (true, "");
    }

    private async Task RecordAndBroadcastAsync(ConsoleSession session, string source, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxConsoleLineChars)
            text = text[..MaxConsoleLineChars] + "…";

        var line = new LogLine(session.ServerId, DateTimeOffset.UtcNow, source, text);
        session.Add(line);
        try { await _hub.Clients.All.SendAsync("logLine", line); }
        catch (Exception ex) { _logger.LogDebug(ex, "Console SignalR broadcast failed for {ServerId}", session.ServerId); }
    }

    private ServerDef? FindServerForExe(string exePath)
        => _registry.All.FirstOrDefault(s => s.HostingMode == HostingMode.BareMetal && PathsEqual(s.GuestExePath, exePath));

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
                var closeSignalSent = false;
                try { closeSignalSent = p.CloseMainWindow(); } catch { /* no GUI window */ }

                // Dedicated servers frequently run without a GUI window. In that case Windows has
                // no graceful WM_CLOSE signal to deliver, so waiting for the old 45-second timeout
                // only made Stop/Restart look broken. Wait briefly only when a real close signal was
                // accepted; otherwise proceed to process-tree termination immediately.
                if (closeSignalSent)
                {
                    var deadline = DateTime.UtcNow.Add(GracefulStopTimeout);
                    while (DateTime.UtcNow < deadline && !p.HasExited)
                        await Task.Delay(250, ct);
                    if (p.HasExited) return true;
                    _logger.LogInformation("Graceful stop timed out for pid {Pid} after {Seconds}s; terminating process tree.", p.Id, GracefulStopTimeout.TotalSeconds);
                }
                else
                {
                    _logger.LogDebug("Process {Pid} has no closeable main window; terminating process tree without waiting.", p.Id);
                }
            }

            try
            {
                p.Kill(entireProcessTree: true);
                await p.WaitForExitAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kill failed for pid {Pid}", p.Id);
                return false;
            }
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

    private sealed class ConsoleSession
    {
        private readonly object _gate = new();
        private readonly List<LogLine> _lines = new();
        private int _chars;
        private volatile bool _exited;

        public ConsoleSession(string serverId, Process process)
        {
            ServerId = serverId;
            Process = process;
        }

        public string ServerId { get; }
        public Process Process { get; }
        public SemaphoreSlim InputGate { get; } = new(1, 1);
        public bool InputAvailable
        {
            get
            {
                if (_exited) return false;
                try { return !Process.HasExited; }
                catch { return false; }
            }
        }

        public void MarkExited() => _exited = true;

        public void Add(LogLine line)
        {
            lock (_gate)
            {
                _lines.Add(line);
                _chars += line.Text.Length;
                while (_lines.Count > MaxConsoleLines || _chars > MaxConsoleChars)
                {
                    _chars -= _lines[0].Text.Length;
                    _lines.RemoveAt(0);
                }
            }
        }

        public LogLine[] Snapshot()
        {
            lock (_gate) return _lines.ToArray();
        }
    }
}
