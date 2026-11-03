using System.Diagnostics;
using System.Runtime.InteropServices;
using GameServerControl.Shared;

namespace GameServerControl.Runner;

public sealed class RunnerProcessHost
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ValheimGracefulStopTimeout = TimeSpan.FromSeconds(20);
    private static readonly SemaphoreSlim ConsoleCtrlGate = new(1, 1);
    private const uint CtrlCEvent = 0;

    private readonly RunnerManifest _manifest;
    private readonly ILogger<RunnerProcessHost> _logger;
    private readonly object _consoleGate = new();
    private readonly SemaphoreSlim _inputGate = new(1, 1);
    private readonly List<RunnerConsoleLine> _consoleLines = new();
    private int _consoleChars;
    private long _nextSequence;
    private Process? _attachedProcess;

    public RunnerProcessHost(RunnerManifest manifest, ILogger<RunnerProcessHost> logger)
    {
        _manifest = manifest;
        _logger = logger;
    }

    public async Task<RunnerResponse> StartAsync(CancellationToken ct)
    {
        var existing = await FindAsync(ct);
        if (!existing.Ok) return existing;
        if (existing.Pid is not null) return existing;

        try
        {
            if (!File.Exists(_manifest.ExecutablePath))
                return new RunnerResponse(false, "Executable not found.");
            if (!Directory.Exists(_manifest.WorkingDirectory))
                return new RunnerResponse(false, "Working directory not found.");

            var interactive = _manifest.InteractiveConsole;
            var psi = new ProcessStartInfo
            {
                FileName = _manifest.ExecutablePath,
                WorkingDirectory = _manifest.WorkingDirectory,
                UseShellExecute = !interactive,
                RedirectStandardInput = interactive,
                RedirectStandardOutput = interactive,
                RedirectStandardError = interactive,
                CreateNoWindow = interactive,
                WindowStyle = interactive ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Minimized
            };
            foreach (var argument in _manifest.Arguments) psi.ArgumentList.Add(argument);

            var process = Process.Start(psi);
            if (process is null) return new RunnerResponse(false, "Process.Start returned null.");
            if (interactive) AttachConsole(process);
            return new RunnerResponse(true, Pid: process.Id, InputAvailable: interactive);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runner failed to start {Executable}", _manifest.ExecutablePath);
            return new RunnerResponse(false, ex.Message);
        }
    }

    public Task<RunnerResponse> FindAsync(CancellationToken ct)
    {
        try
        {
            var attached = _attachedProcess;
            if (attached is not null)
            {
                try
                {
                    if (!attached.HasExited)
                        return Task.FromResult(new RunnerResponse(true, Pid: attached.Id, InputAvailable: _manifest.InteractiveConsole));
                }
                catch { }
            }

            var name = Path.GetFileNameWithoutExtension(_manifest.ExecutablePath);
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    if (PathsEqual(process.MainModule?.FileName, _manifest.ExecutablePath))
                        return Task.FromResult(new RunnerResponse(true, Pid: process.Id, InputAvailable: false));
                }
                catch { }
                finally
                {
                    if (_attachedProcess is null || process.Id != _attachedProcess.Id) process.Dispose();
                }
            }
            return Task.FromResult(new RunnerResponse(true));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runner process lookup failed");
            return Task.FromResult(new RunnerResponse(false, ex.Message));
        }
    }

    public async Task<RunnerResponse> StopAsync(bool force, CancellationToken ct)
    {
        var process = ResolveAttachedProcess();
        if (process is null)
        {
            var found = await FindAsync(ct);
            if (!found.Ok) return found;
            if (found.Pid is null) return new RunnerResponse(true);
            try { process = Process.GetProcessById(found.Pid.Value); }
            catch { return new RunnerResponse(true); }
        }

        try
        {
            if (!force)
            {
                if (_manifest.SteamAppId == "896660" && await TrySendValheimInterruptAsync(process, ct))
                {
                    if (await WaitForExitAsync(process, ValheimGracefulStopTimeout, ct))
                        return new RunnerResponse(true);
                }

                var closeSent = false;
                try { closeSent = process.CloseMainWindow(); } catch { }
                if (closeSent && await WaitForExitAsync(process, GracefulStopTimeout, ct))
                    return new RunnerResponse(true);
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
            return new RunnerResponse(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runner failed to stop pid {Pid}", SafePid(process));
            return new RunnerResponse(false, ex.Message);
        }
    }

    public RunnerResponse ConsoleState(long? afterSequence = null)
    {
        var snapshot = SnapshotConsole(afterSequence);
        if (!_manifest.InteractiveConsole)
            return new RunnerResponse(true, Console: new RunnerConsoleSnapshot(
                false, false, "This server does not use a stdin console.", snapshot.LastSequence, snapshot.Lines));

        var attached = ResolveAttachedProcess();
        if (attached is null)
            return new RunnerResponse(true, Console: new RunnerConsoleSnapshot(
                true, false, "No attached console. Restart the server through GameServerControl to attach stdin.", snapshot.LastSequence, snapshot.Lines));

        return new RunnerResponse(true, InputAvailable: true, Console: new RunnerConsoleSnapshot(
            true, true, "Interactive console attached.", snapshot.LastSequence, snapshot.Lines));
    }

    public async Task<RunnerResponse> SendConsoleInputAsync(string? input, CancellationToken ct)
    {
        var request = new RunnerRequest(RunnerCommand.SendConsoleInput, ConsoleInput: input);
        var validation = request.Validate();
        if (validation is not null) return new RunnerResponse(false, validation);
        if (input is null || input.Length > RunnerProtocolLimits.MaxConsoleInputChars)
            return new RunnerResponse(false, "Invalid console input.");

        var process = ResolveAttachedProcess();
        if (!_manifest.InteractiveConsole || process is null)
            return new RunnerResponse(false, "No attached console session.");

        await _inputGate.WaitAsync(ct);
        try
        {
            if (process.HasExited) return new RunnerResponse(false, "Console process has exited.");
            await process.StandardInput.WriteLineAsync(input.Trim());
            await process.StandardInput.FlushAsync();
            RecordConsole("stdin", "> " + input.Trim());
            return new RunnerResponse(true, Pid: process.Id, InputAvailable: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Runner console input failed");
            return new RunnerResponse(false, ex.Message);
        }
        finally { _inputGate.Release(); }
    }

    public async Task<RunnerResponse> KillExtrasAsync(CancellationToken ct)
    {
        foreach (var raw in (_manifest.StopProcessNames ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var process in Process.GetProcessesByName(name))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(ct);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Runner could not kill extra process {Name}", name); }
                finally { process.Dispose(); }
            }
        }
        return new RunnerResponse(true);
    }

    private void AttachConsole(Process process)
    {
        _attachedProcess = process;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) RecordConsole("stdout", e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) RecordConsole("stderr", e.Data); };
        process.Exited += (_, _) =>
        {
            var code = "?";
            try { code = process.ExitCode.ToString(); } catch { }
            RecordConsole("process", $"[process exited with code {code}]");
            if (ReferenceEquals(_attachedProcess, process)) _attachedProcess = null;
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        RecordConsole("process", $"[interactive console attached — pid {process.Id}]");
    }

    private Process? ResolveAttachedProcess()
    {
        var process = _attachedProcess;
        if (process is null) return null;
        try { return process.HasExited ? null : process; }
        catch { return null; }
    }

    private void RecordConsole(string source, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > RunnerProtocolLimits.MaxConsoleLineChars)
            text = text[..RunnerProtocolLimits.MaxConsoleLineChars] + "…";

        lock (_consoleGate)
        {
            var line = new RunnerConsoleLine(++_nextSequence, DateTimeOffset.UtcNow, source, text);
            _consoleLines.Add(line);
            _consoleChars += text.Length;
            while (_consoleLines.Count > RunnerProtocolLimits.MaxConsoleLines || _consoleChars > RunnerProtocolLimits.MaxConsoleChars)
            {
                _consoleChars -= _consoleLines[0].Text.Length;
                _consoleLines.RemoveAt(0);
            }
        }
    }

    private (long LastSequence, RunnerConsoleLine[] Lines) SnapshotConsole(long? afterSequence)
    {
        lock (_consoleGate)
        {
            var lines = afterSequence is null
                ? _consoleLines.ToArray()
                : _consoleLines.Where(line => line.Sequence > afterSequence.Value).ToArray();
            return (_nextSequence, lines);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { if (process.HasExited) return true; } catch { return true; }
            await Task.Delay(250, ct);
        }
        try { return process.HasExited; } catch { return true; }
    }

    private async Task<bool> TrySendValheimInterruptAsync(Process process, CancellationToken ct)
    {
        await ConsoleCtrlGate.WaitAsync(ct);
        var attached = false;
        try
        {
            attached = AttachConsole((uint)process.Id);
            if (!attached) return false;
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            var sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            if (sent) await Task.Delay(250, ct);
            return sent;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runner CTRL+C delivery to Valheim pid {Pid} failed", SafePid(process));
            return false;
        }
        finally
        {
            if (attached) FreeConsole();
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            ConsoleCtrlGate.Release();
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
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static int SafePid(Process process)
    {
        try { return process.Id; } catch { return -1; }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, bool add);
}
