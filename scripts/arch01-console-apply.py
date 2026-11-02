from pathlib import Path

manager = Path('src/GameServerControl.Agent/Hyperv/WindowsRunnerServiceManager.cs')
text = manager.read_text()
old = '''    public async Task<RunnerResponse> ConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.ConsoleState), ct);

    public async Task<RunnerResponse> SendConsoleInputAsync'''
new = '''    public async Task<RunnerResponse> ConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        await SendForServerAsync(def, new RunnerRequest(RunnerCommand.ConsoleState), ct);

    internal Task<RunnerResponse> PollConsoleStateAsync(ServerDef def, CancellationToken ct = default) =>
        _client.SendAsync(RunnerIdentity.ForServer(def.Id), new RunnerRequest(RunnerCommand.ConsoleState), ct);

    public async Task<RunnerResponse> SendConsoleInputAsync'''
if new not in text:
    if old not in text:
        raise SystemExit('WindowsRunnerServiceManager console state fragment not found')
    text = text.replace(old, new, 1)
manager.write_text(text)

local = Path('src/GameServerControl.Agent/Hyperv/LocalProcessService.cs')
text = local.read_text()
old = '''    private readonly WindowsRunnerServiceManager _windowsRunner;
    private readonly ConcurrentDictionary<string, ConsoleSession> _consoleSessions = new(StringComparer.OrdinalIgnoreCase);'''
new = '''    private readonly WindowsRunnerServiceManager _windowsRunner;
    private readonly ConcurrentDictionary<string, ConsoleSession> _consoleSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runnerConsolePumps = new(StringComparer.OrdinalIgnoreCase);'''
if new not in text:
    if old not in text:
        raise SystemExit('LocalProcessService field fragment not found')
    text = text.replace(old, new, 1)

old = '''            var snapshot = response.Console;
            if (snapshot is null)
                return new LocalConsoleState(true, false, "Runner returned no console state.", Array.Empty<LogLine>());
            var lines = snapshot.Lines.Select(line => new LogLine(def.Id, line.At, line.Source, line.Text)).ToArray();
            return new LocalConsoleState(snapshot.Supported, snapshot.InputAvailable, snapshot.Message, lines);
        }

        return GetLocalConsoleState(def);
    }

    private LocalConsoleState GetLocalConsoleState'''
new = '''            var snapshot = response.Console;
            if (snapshot is null)
                return new LocalConsoleState(true, false, "Runner returned no console state.", Array.Empty<LogLine>());
            var lines = snapshot.Lines.Select(line => new LogLine(def.Id, line.At, line.Source, line.Text)).ToArray();
            if (snapshot.InputAvailable) EnsureRunnerConsolePump(def, snapshot.LastSequence);
            return new LocalConsoleState(snapshot.Supported, snapshot.InputAvailable, snapshot.Message, lines);
        }

        return GetLocalConsoleState(def);
    }

    private void EnsureRunnerConsolePump(ServerDef def, long lastSequence)
    {
        var cts = new CancellationTokenSource();
        if (!_runnerConsolePumps.TryAdd(def.Id, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(() => PumpRunnerConsoleAsync(def, lastSequence, cts), CancellationToken.None);
    }

    private async Task PumpRunnerConsoleAsync(ServerDef def, long lastSequence, CancellationTokenSource pumpCts)
    {
        var ct = pumpCts.Token;
        var consecutiveFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var response = await _windowsRunner.PollConsoleStateAsync(def, ct);
                if (!response.Ok || response.Console is null)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 20) break;
                    await Task.Delay(500, ct);
                    continue;
                }

                consecutiveFailures = 0;
                var snapshot = response.Console;
                foreach (var runnerLine in snapshot.Lines
                             .Where(line => line.Sequence > lastSequence)
                             .OrderBy(line => line.Sequence))
                {
                    var line = new LogLine(def.Id, runnerLine.At, runnerLine.Source, runnerLine.Text);
                    try
                    {
                        await _hub.Clients.Group(StatusHub.ServerGroup(def.Id)).SendAsync("logLine", line, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Runner console SignalR broadcast failed for {ServerId}", def.Id);
                    }
                    lastSequence = runnerLine.Sequence;
                }

                if (!snapshot.InputAvailable) break;
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runner console pump failed for {ServerId}", def.Id);
        }
        finally
        {
            if (_runnerConsolePumps.TryRemove(def.Id, out var removed)) removed.Dispose();
        }
    }

    private LocalConsoleState GetLocalConsoleState'''
if new not in text:
    if old not in text:
        raise SystemExit('LocalProcessService console state fragment not found')
    text = text.replace(old, new, 1)

local.write_text(text)
